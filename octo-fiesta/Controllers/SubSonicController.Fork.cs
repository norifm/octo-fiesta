using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using octo_fiesta.Models.Domain;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Subsonic;

namespace octo_fiesta.Controllers;

// Fork additions to the album and cover art endpoints, kept out of SubSonicController.cs so syncing
// with upstream touches only one-line hooks there.
public partial class SubsonicController
{
    /// <summary>
    /// The library album holding a downloaded track of this provider album, or null. Only our
    /// own mappings are read, and the library album must carry the provider album's title,
    /// so a track shared with another release does not pull in that release's album.
    /// </summary>
    private async Task<string?> FindOwnedAlbumIdAsync(Album album, Dictionary<string, string> parameters)
    {
        var mapped = new List<string>();
        foreach (var song in album.Songs.Where(s => s.ExternalProvider != null && s.ExternalId != null))
        {
            var mapping = await _localLibraryService.GetMappingForExternalSongAsync(song.ExternalProvider!, song.ExternalId!);
            if (!string.IsNullOrEmpty(mapping?.LocalSubsonicId))
            {
                mapped.Add(mapping.LocalSubsonicId);
            }
        }

        var albumKey = StringNormalizer.CreateComparisonKey(album.Title);
        foreach (var localSongId in mapped.Take(3))
        {
            var lookup = new Dictionary<string, string>(parameters) { ["id"] = localSongId, ["f"] = "json" };
            var result = await _proxyService.RelaySafeAsync("rest/getSong", lookup);
            if (!result.Success || result.Body == null)
            {
                continue;
            }

            using var doc = JsonDocument.Parse(result.Body);
            if (doc.RootElement.TryGetProperty("subsonic-response", out var response)
                && response.TryGetProperty("song", out var song)
                && song.TryGetProperty("albumId", out var albumId)
                && song.TryGetProperty("album", out var albumName)
                && !string.IsNullOrEmpty(albumId.GetString())
                && (StringNormalizer.CreateComparisonKey(albumName.GetString()) == albumKey
                    || IsSameAlbumWithEditionSuffix(albumName.GetString(), album.Title)))
            {
                return albumId.GetString();
            }
        }

        return null;
    }

    // How long an album page waits for a provider the first time it is asked about; after
    // that the answer is cached and the page never waits.
    private static readonly TimeSpan FirstLookupBudget = TimeSpan.FromSeconds(2);

    private async Task<Album?> FindExternalAlbumAsync(string artistName, string albumName, List<string> localTitles)
    {
        Task<Album?> Resolve() => ResolveExternalAlbumAsync(artistName, albumName, localTitles);

        if (_albumMergeCache == null)
        {
            return await Resolve();
        }

        var key = $"{StringNormalizer.CreateComparisonKey(artistName)}|{StringNormalizer.CreateComparisonKey(albumName)}|{localTitles.Count}";
        if (_albumMergeCache.TryGet(key, out var cached, out var stale))
        {
            if (stale)
            {
                ObserveBackground(_albumMergeCache.ResolveAsync(key, Resolve));
            }
            return cached;
        }

        var lookup = _albumMergeCache.ResolveAsync(key, Resolve);
        ObserveBackground(lookup);
        var first = await Task.WhenAny(lookup, Task.Delay(FirstLookupBudget));
        return first == lookup && lookup.IsCompletedSuccessfully ? lookup.Result : null;
    }

    private void ObserveBackground(Task task) =>
        task.ContinueWith(
            t => _logger.LogWarning(t.Exception?.GetBaseException(), "Album merge lookup failed"),
            TaskContinuationOptions.OnlyOnFaulted);

    /// <summary>
    /// The provider album this library album is, or null: the same artist, the same title
    /// (or the title plus an edition suffix) and tracks in common with the library album.
    /// </summary>
    private async Task<Album?> ResolveExternalAlbumAsync(string artistName, string albumName, List<string> localTitles)
    {
        var externalAlbumsSearch = await _metadataService.SearchAlbumsAsync($"{artistName} {albumName}", 5);

        // Only a candidate credited to the same artist can be merged, otherwise a homonym
        // such as "Gary Grimes" pours its tracks into an album by "Grimes".
        var sameArtistCandidates = externalAlbumsSearch
            .Where(c => IsSameArtistOrCollaboration(c.Artist, artistName))
            .ToList();

        var albumKey = StringNormalizer.CreateComparisonKey(albumName);
        var exact = sameArtistCandidates.Where(c => StringNormalizer.CreateComparisonKey(c.Title) == albumKey).ToList();
        var editions = sameArtistCandidates.Except(exact).Where(c => IsSameAlbumWithEditionSuffix(c.Title, albumName));

        foreach (var candidate in exact.Concat(editions).Take(3))
        {
            var album = await _metadataService.GetAlbumAsync(candidate.ExternalProvider!, candidate.ExternalId!);
            if (album != null && ExternalAlbumMatcher.SharesTracks(localTitles, album.Songs))
            {
                return album;
            }
        }

        return null;
    }

    /// <summary>
    /// The provider album to merge into a library album, and the library's song title keys (comparison
    /// and dedupe keys) so tracks the library already holds are not listed twice.
    /// </summary>
    private async Task<(Album? ExternalAlbum, HashSet<string> LocalSongTitles)> ForkFindExternalAlbumAsync(
        string artistName, string albumName, List<object> localSongs, XElement? albumXml)
    {
        var localSongTitles = new HashSet<string>();
        var localTitles = new List<string>();
        foreach (var song in localSongs)
        {
            if (song is Dictionary<string, object> dict && dict.TryGetValue("title", out var titleObj))
            {
                var title = titleObj?.ToString() ?? "";
                localTitles.Add(title);
                localSongTitles.Add(StringNormalizer.CreateComparisonKey(title));
                localSongTitles.Add(StringNormalizer.CreateSongTitleDedupeKey(title));
            }
        }
        foreach (var song in ChildElements(albumXml, "song"))
        {
            var title = song.Attribute("title")?.Value;
            localTitles.Add(title ?? "");
            localSongTitles.Add(StringNormalizer.CreateComparisonKey(title));
            localSongTitles.Add(StringNormalizer.CreateSongTitleDedupeKey(title));
        }

        return (await FindExternalAlbumAsync(artistName, albumName, localTitles), localSongTitles);
    }

    /// <summary>
    /// Cover art through <see cref="IExternalCoverArtService"/>: local covers are relayed to the Subsonic
    /// server, external and playlist covers are resolved (and resized to the requested size) by the service.
    /// </summary>
    private async Task<IActionResult> ForkGetCoverArtAsync(IExternalCoverArtService externalCoverArtService, string id, Dictionary<string, string> parameters)
    {
        // Local (Subsonic) covers: relay through to upstream Subsonic (Navidrome).
        var isPlaylist = PlaylistIdHelper.IsExternalPlaylist(id);
        (bool isExternal, string? provider, string? type, string? externalId) parsedExternalId = default;
        if (!isPlaylist)
        {
            parsedExternalId = _localLibraryService.ParseExternalId(id);
            if (!parsedExternalId.isExternal)
            {
                try
                {
                    var result = await _proxyService.RelayAsync("rest/getCoverArt", parameters);
                    return File(result.Body, result.ContentType ?? "image/jpeg");
                }
                catch
                {
                    return NotFound();
                }
            }
        }

        // Honour Subsonic `size` parameter so e.g. album-list thumbnails fetch a much
        // smaller image from the upstream CDN. Qobuz CDN URLs are size-rewritable
        // (`{id}_600.jpg` -> `{id}_150.jpg`) without needing an extra API call.
        int? requestedSize = null;
        if (parameters.TryGetValue("size", out var sizeStr) && int.TryParse(sizeStr, out var s) && s > 0)
        {
            requestedSize = s;
        }

        var payload = await externalCoverArtService.ResolveAsync(
            id,
            parsedExternalId,
            requestedSize,
            HttpContext.RequestAborted);
        if (payload == null)
        {
            return NotFound();
        }

        return File(payload.Bytes, payload.ContentType);
    }
}

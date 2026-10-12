using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Subsonic;

namespace octo_fiesta.Services.GDStudio;

/// <summary>
/// Metadata via the GD Studio API. Upstream only has track search (plus "_album" search that
/// lists tracks of matching albums), so albums/artists are derived from track results and
/// identified by name, and found songs are remembered to serve GetSongAsync.
/// </summary>
public class GDStudioMetadataService : IMusicMetadataService, IDisposable
{
    public const string ProviderName = "gdstudio";

    private readonly HttpClient _http;
    private readonly ILogger<GDStudioMetadataService> _logger;
    private readonly GDStudioSettings _s;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 2048 });
    private readonly object _cacheLock = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private void RememberAlbum(string key, IEnumerable<GDStudioTrack> tracks)
    {
        lock (_cacheLock)
        {
            _cache.TryGetValue<List<GDStudioTrack>>("album:" + key, out var old);
            var merged = (old ?? []).Concat(tracks).DistinctBy(t => t.Id).Take(1000).ToList();
            _cache.Set("album:" + key, merged, new MemoryCacheEntryOptions()
                .SetSize(Math.Max(1, merged.Count)).SetAbsoluteExpiration(CacheTtl));
        }
    }

    public GDStudioMetadataService(
        IHttpClientFactory httpClientFactory,
        IOptions<GDStudioSettings> settings,
        ILogger<GDStudioMetadataService> logger)
    {
        _http = httpClientFactory.CreateClient(GDStudioHttpClientConfiguration.ClientName);
        _logger = logger;
        _s = settings.Value;
    }

    // Albums and artists have no ids upstream: the id is the base64url-encoded name.
    private const string AlbumPrefix = "ext-gdstudio-album-";
    private const string ArtistPrefix = "ext-gdstudio-artist-";

    private static string Enc(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Dec(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')));
    }

    // The API answers 422 for count > 99.
    private const int MaxCount = 99;

    // The first call to each source (+ suffix) may have to warm up a plugin (e.g. download and run the
    // site's signing script), so it gets three times the normal timeout.
    private readonly ConcurrentDictionary<string, byte> _warmed = new();

    // The API rate limits (about 50 requests per 5 minutes, then 503/429), and a client that opens an
    // artist page asks for every album at once. So: identical queries are shared and remembered for an
    // hour (in flight or done, never failures), at most MaxParallel run at a time, and a 503/429
    // is retried once after a short pause.
    private const int MaxParallel = 4;
    private readonly SemaphoreSlim _gate = new(MaxParallel);
    private Task<List<GDStudioTrack>> Fetch(string api, string name, int count, int page, TimeSpan timeout)
    {
        var key = $"query:{api}\n{count}\n{page}\n{name}";
        lock (_cacheLock)
        {
            if (_cache.TryGetValue<Task<List<GDStudioTrack>>>(key, out var hit) && hit is not null
                && !hit.IsFaulted && !hit.IsCanceled) return hit;
            var task = FetchCore(api, name, count, page, timeout);
            _cache.Set(key, task, new MemoryCacheEntryOptions().SetSize(1).SetAbsoluteExpiration(CacheTtl));
            return task;
        }
    }

    private async Task<List<GDStudioTrack>> FetchCore(string api, string name, int count, int page, TimeSpan timeout)
    {
        var url = _s.Url($"types=search&source={Uri.EscapeDataString(api)}"
                  + $"&name={Uri.EscapeDataString(name)}&count={count}" + (page > 1 ? $"&pages={page}" : ""));
        // Time spent queued for a slot does not count against the request timeout, only the request and its retry do
        using (var queue = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            await _gate.WaitAsync(queue.Token);
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            _logger.LogInformation("GDStudio request {Source} '{Query}' page {Page} (a real API call, not cached)", api, name, page);
            for (var attempt = 0; ; attempt++)
            {
                using var response = await _http.GetAsync(url, cts.Token);
                if (attempt == 0 && response.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.TooManyRequests)
                {
                    var pause = response.Headers.RetryAfter?.Delta is { } d && d < TimeSpan.FromSeconds(3) ? d : TimeSpan.FromSeconds(1);
                    _logger.LogWarning("GDStudio {Source} '{Query}' got {Status}, retrying once in {Ms} ms", api, name, (int)response.StatusCode, (int)pause.TotalMilliseconds);
                    await Task.Delay(pause, cts.Token);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<List<GDStudioTrack>>(cts.Token) ?? [];
            }
        }
        finally { _gate.Release(); }
    }

    // Largest count per API source (source + suffix, e.g. "apple" or "netease_album"); the API itself refuses
    // more than MaxCount (422). Sources that accept less without an error (apple answers count > 50 with an
    // empty list, HTTP 200) are listed here so they are never asked for too much, and every further request
    // saves a call. Unlisted sources are checked once: when a big first page comes back empty it is asked
    // again with SafeCount, and if that returns tracks the source is added to the map at SafeCount.
    private static readonly IReadOnlyDictionary<string, int> KnownMaxCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["apple"] = 50,
    };
    private const int SafeCount = 50;
    private readonly ConcurrentDictionary<string, int> _maxCount = new(KnownMaxCount, StringComparer.OrdinalIgnoreCase);

    // One page of one source (`suffix` selects e.g. "_album"). Failures and timeouts are logged, not thrown:
    // Ok is false and Tracks is empty, so the caller can keep what the other sources returned.
    // Asked is the count actually used, which is what a "last page" check has to compare against.
    private async Task<(List<GDStudioTrack> Tracks, bool Ok, int Asked)> QuerySourceAsync(string source, string suffix, string name, int count, int page)
    {
        var api = source + suffix; // what the API sees, e.g. netease_album
        var seconds = _warmed.TryAdd(api, 0) ? _s.TimeoutSeconds * 3 : _s.TimeoutSeconds;
        var timeout = TimeSpan.FromSeconds(seconds);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var asked = _maxCount.TryGetValue(api, out var max) ? Math.Min(count, max) : count;
        try
        {
            var results = await Fetch(api, name, asked, page, timeout);
            if (results.Count == 0 && asked > SafeCount && page == 1)
            {
                var smaller = await Fetch(api, name, SafeCount, page, timeout);
                if (smaller.Count > 0) // the big count was the problem, not an empty result: cap this source
                {
                    _logger.LogWarning("GDStudio {Source} returned nothing for count={Count} but {Results} for {Safe}, limiting this source to {Safe}", api, asked, smaller.Count, SafeCount, SafeCount);
                    _maxCount[api] = asked = SafeCount;
                    results = smaller;
                }
            }
            _logger.LogInformation("GDStudio {Source} '{Query}' (count={Count}, page={Page}) -> {Results} results in {Ms} ms",
                api, name, asked, page, results.Count, watch.ElapsedMilliseconds);
            return (results.Where(t => !string.IsNullOrEmpty(t.Id))
                .Select(t => t with { Id = _s.TrackId(source, t.Id) }).ToList(), true, asked);
        }
        catch (OperationCanceledException)
        {
            _logger.LogError("GDStudio source '{Source}' timed out after {Seconds}s for '{Query}' (page {Page})", api, seconds, name, page);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GDStudio source '{Source}' failed for '{Query}' (page {Page})", api, name, page);
        }
        return ([], false, asked);
    }

    // Queries every configured source in parallel and returns only when all of them have answered (or failed),
    // interleaving the results so each source is represented.
    private async Task<List<GDStudioTrack>> QueryAsync(string name, int count, string suffix = "")
    {
        if (count <= 0) return [];
        count = Math.Min(count, MaxCount);
        var lists = await Task.WhenAll(_s.Sources.Select(async source => (await QuerySourceAsync(source, suffix, name, count, 1)).Tracks));
        return lists.SelectMany(l => l.Select((t, i) => (t, i))).OrderBy(x => x.i).Select(x => x.t).ToList();
    }

    // Search listings are partial catalogue views. Fetch album pages explicitly when opening an
    // album, rather than treating tracks seen in an artist/search result as its complete contents.
    private async Task<List<GDStudioTrack>> QueryPagesAsync(string name, string suffix, int maxPages)
    {
        async Task<List<GDStudioTrack>> Source(string source)
        {
            var collected = new List<GDStudioTrack>();
            for (var page = 1; page <= maxPages; page++)
            {
                var (tracks, ok, asked) = await QuerySourceAsync(source, suffix, name, MaxCount, page);
                if (!ok) break; // Failed pages are not cached; the next request retries them.
                var before = collected.Count;
                collected.AddRange(tracks.Where(t => collected.All(old => old.Id != t.Id)));
                if (tracks.Count < asked || collected.Count == before) break;
            }
            return collected;
        }
        var lists = await Task.WhenAll(_s.Sources.Select(Source));
        return lists.SelectMany(l => l.Select((t, i) => (t, i))).OrderBy(x => x.i).Select(x => x.t).ToList();
    }

    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20)
        => (await QueryAsync(query, limit)).Take(Math.Max(0, limit)).Select(ToSong).ToList();

    // An album is identified by its name plus its first artist, so different releases that share a name
    // (every artist has a "素颜") stay apart. Ids without an artist part are still understood.
    private static string AlbumKey(string album, string? artist) => Enc(artist is null ? album : album + "\t" + artist);

    private static (string Name, string? Artist) SplitAlbumKey(string encoded)
    {
        var text = Dec(encoded);
        var i = text.IndexOf('\t');
        return i < 0 ? (text, null) : (text[..i], text[(i + 1)..]);
    }

    // Group search results for browsing; these do not establish a complete track list.
    private List<Album> AlbumsFrom(IEnumerable<GDStudioTrack> tracks)
        => tracks.GroupBy(t => (t.Album, Artist: t.Artist.FirstOrDefault())).Where(g => g.Key.Album != "").Select(g =>
        {
            var key = AlbumKey(g.Key.Album, g.Key.Artist);
            RememberAlbum(key, g);
            return new Album
            {
                Id = AlbumPrefix + key,
                Title = g.Key.Album,
                Artist = g.Key.Artist ?? "",
                ArtistId = g.Key.Artist is null ? null : ArtistPrefix + Enc(g.Key.Artist),
                SongCount = g.Count(),
                ExternalProvider = ProviderName,
                ExternalId = key
            };
        }).ToList();

    public async Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20)
        => AlbumsFrom(await QueryAsync(query, 50, "_album")).Take(limit).ToList();

    public async Task<Album?> GetAlbumAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName) return null;
        var (name, artist) = SplitAlbumKey(externalId);
        _cache.TryGetValue<List<GDStudioTrack>>("album:" + externalId, out var cached);
        bool IsThisAlbum(GDStudioTrack t) => t.Album == name && (artist is null || t.Artist.FirstOrDefault() == artist);
        var tracks = (await QueryPagesAsync(name, "_album", 10)).Concat(cached ?? [])
            .DistinctBy(t => t.Id).Where(IsThisAlbum).ToList();
        if (tracks.Count == 0)
            tracks = (await QueryAsync(artist is null ? name : $"{artist} {name}", MaxCount)).Where(IsThisAlbum).ToList();
        // Different sources carry alternative copies. Use the first configured source with matches
        // for an album, so every song is not downloaded once per upstream source.
        if (tracks.Count > 0)
        {
            var source = _s.SplitTrackId(tracks[0].Id).Source;
            tracks = tracks.Where(t => _s.SplitTrackId(t.Id).Source == source).ToList();
        }
        if (tracks.Count == 0) return null;
        var songs = tracks.Select(ToSong).ToList();
        for (var i = 0; i < songs.Count; i++) { songs[i].Track = i + 1; songs[i].TotalTracks = songs.Count; }
        return new Album
        {
            Id = AlbumPrefix + externalId,
            Title = name,
            Artist = songs[0].AlbumArtist ?? "",
            ArtistId = songs[0].ArtistId,
            SongCount = songs.Count,
            ExternalProvider = ProviderName,
            ExternalId = externalId,
            Songs = songs
        };
    }

    public async Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20)
        => (await QueryAsync(query, 50)).SelectMany(t => t.Artist).Distinct().Take(limit).Select(ToArtist).ToList();

    public Task<Artist?> GetArtistAsync(string externalProvider, string externalId)
        => Task.FromResult(externalProvider == ProviderName ? ToArtist(Dec(externalId)) : null);

    public async Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName) return [];
        var name = Dec(externalId);
        var aliases = _s.ArtistAliases.TryGetValue(name, out var names) ? names : [];
        bool Matches(string artist) => artist.Equals(name, StringComparison.OrdinalIgnoreCase)
            || aliases.Any(alias => artist.Equals(alias, StringComparison.OrdinalIgnoreCase));
        return AlbumsFrom((await QueryPagesAsync(name, "", 3)).Where(t => t.Artist.Any(Matches)));
    }

    public async Task<string?> GetAlbumCoverUrlAsync(string externalProvider, string externalId)
    {
        var album = await GetAlbumAsync(externalProvider, externalId);
        var first = album?.Songs.FirstOrDefault();
        return first?.ExternalId is { } id ? (await GetSongAsync(externalProvider, id))?.CoverArtUrl : null;
    }

    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20)
    {
        var songs = (await QueryAsync(query, Math.Max(songLimit, artistLimit))).ToList();
        return new SearchResult
        {
            Songs = songs.Take(songLimit).Select(ToSong).ToList(),
            Artists = songs.SelectMany(t => t.Artist).Distinct().Take(artistLimit).Select(ToArtist).ToList(),
            Albums = await SearchAlbumsAsync(query, albumLimit)
        };
    }

    // No get-by-id upstream ("url" only returns the file link), so serve what we've seen in
    // searches/albums. Unknown ids require a fresh search before downloading.
    public async Task<Song?> GetSongAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName) return null;
        if (!_cache.TryGetValue<Song>("song:" + externalId, out var song) || song is null)
            return null; // An unknown id must not create an incorrectly tagged download.
        if (song.CoverArtUrlLarge is null && _cache.TryGetValue<string>("pic:" + externalId, out var pic) && pic is not null)
        {
            song.CoverArtUrlLarge = song.CoverArtUrl = await ResolvePicAsync(externalId, pic);
        }
        return song;
    }

    public Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20) => Task.FromResult(new List<ExternalPlaylist>());
    public Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId) => Task.FromResult<ExternalPlaylist?>(null);
    public Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId) => Task.FromResult(new List<Song>());

    private async Task<string?> ResolvePicAsync(string trackId, string picId)
    {
        if (picId.StartsWith("//")) return "https:" + picId;
        if (picId.StartsWith("http")) return picId;
        try
        {
            var (source, _) = _s.SplitTrackId(trackId);
            var r = await _http.GetFromJsonAsync<GDStudioPic>(
                _s.Url($"types=pic&source={Uri.EscapeDataString(source)}&id={Uri.EscapeDataString(picId)}&size=500"));
            return string.IsNullOrEmpty(r?.Url) ? null : r.Url;
        }
        catch { return null; }
    }

    private static Artist ToArtist(string name) => new()
    {
        Id = ArtistPrefix + Enc(name), Name = name, ExternalProvider = ProviderName, ExternalId = Enc(name)
    };

    public async Task<string?> GetLyricsAsync(string trackId)
    {
        try
        {
            var (source, id) = _s.SplitTrackId(trackId);
            var r = await _http.GetFromJsonAsync<GDStudioLyric>(
                _s.Url($"types=lyric&source={Uri.EscapeDataString(source)}&id={Uri.EscapeDataString(id)}"));
            return string.IsNullOrWhiteSpace(r?.Lyric) ? null : r.Lyric;
        }
        catch { return null; }
    }

    private Song ToSong(GDStudioTrack t)
    {
        var artists = t.Artist.Select(ToArtist).ToList();
        var song = new Song
        {
            Title = t.Name,
            Artist = string.Join(", ", t.Artist),
            Artists = artists,
            ArtistId = artists.FirstOrDefault()?.Id,
            Album = t.Album,
            AlbumId = t.Album == "" ? null : AlbumPrefix + AlbumKey(t.Album, t.Artist.FirstOrDefault()),
            AlbumArtist = t.Artist.FirstOrDefault(),
            IsLocal = false,
            ExternalProvider = ProviderName,
            ExternalId = t.Id
        };
        _cache.Set("song:" + t.Id, song, new MemoryCacheEntryOptions().SetSize(1).SetAbsoluteExpiration(CacheTtl));
        if (t.Album != "") RememberAlbum(AlbumKey(t.Album, t.Artist.FirstOrDefault()), [t]);
        if (!string.IsNullOrEmpty(t.PicId)) _cache.Set("pic:" + t.Id, t.PicId, new MemoryCacheEntryOptions().SetSize(1).SetAbsoluteExpiration(CacheTtl));
        return song;
    }

    public void Dispose()
    {
        _cache.Dispose();
        _gate.Dispose();
    }

    private record GDStudioTrack(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("artist")] List<string> Artist,
        [property: JsonPropertyName("album")] string Album,
        [property: JsonPropertyName("pic_id")] string? PicId);

    private record GDStudioPic([property: JsonPropertyName("url")] string? Url);

    private record GDStudioLyric([property: JsonPropertyName("lyric")] string? Lyric);
}

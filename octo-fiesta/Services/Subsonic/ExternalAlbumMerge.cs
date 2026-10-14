using Microsoft.Extensions.Caching.Memory;
using octo_fiesta.Models.Domain;
using octo_fiesta.Services.Common;

namespace octo_fiesta.Services.Subsonic;

/// <summary>
/// Decides which provider album a library album is, so its missing tracks can be listed.
/// </summary>
public static class ExternalAlbumMatcher
{
    private static readonly string[] EditionWords =
    {
        "deluxe", "expanded", "edition", "remaster", "anniversary", "bonus", "special",
        "extended", "reissue", "collector", "platinum", "complete", "version"
    };

    // A release that shares the title but is another record, however it is worded.
    private static readonly string[] OtherReleaseWords =
    {
        "live", "remix", "single", "ep", "instrumental", "acoustic", "karaoke", "demo",
        "session", "rarities", "unplugged", "tribute", "cover"
    };

    /// <summary>
    /// True when the suffix after a shared title only names an edition ("(Deluxe Edition)"),
    /// not another release ("- Single", ": Live from ...").
    /// </summary>
    public static bool IsEditionSuffix(string suffix)
    {
        var words = suffix.Split(new[] { ' ', '(', ')', '[', ']', '-', ':', ',', '.', '/' }, StringSplitOptions.RemoveEmptyEntries);
        return !words.Any(w => OtherReleaseWords.Contains(w))
            && words.Any(w => EditionWords.Any(e => w.StartsWith(e, StringComparison.Ordinal)));
    }

    /// <summary>
    /// True when at least half of the library titles, and one at least, are on the candidate.
    /// A look-alike release that shares no tracks with the album is not that album.
    /// </summary>
    public static bool SharesTracks(IReadOnlyCollection<string> localTitles, IEnumerable<Song> candidateSongs)
    {
        if (localTitles.Count == 0)
        {
            return false;
        }

        var candidateKeys = new HashSet<string>();
        foreach (var song in candidateSongs)
        {
            candidateKeys.Add(StringNormalizer.CreateComparisonKey(song.Title));
            candidateKeys.Add(StringNormalizer.CreateSongTitleDedupeKey(song.Title));
        }

        var shared = localTitles.Count(t => candidateKeys.Contains(StringNormalizer.CreateComparisonKey(t))
            || candidateKeys.Contains(StringNormalizer.CreateSongTitleDedupeKey(t)));
        return shared > 0 && shared * 2 >= localTitles.Count;
    }
}

/// <summary>
/// Remembers which provider album matched a library album (or that none did), so the
/// album page does not wait for the provider on every view. A stale answer is still
/// served while it is refreshed in the background.
/// </summary>
public class ExternalAlbumMergeCache
{
    private static readonly TimeSpan MatchTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan MissTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan Refresh = TimeSpan.FromHours(6);

    private readonly IMemoryCache _cache;
    private readonly Dictionary<string, Task<Album?>> _inFlight = new();

    public ExternalAlbumMergeCache(IMemoryCache cache)
    {
        _cache = cache;
    }

    private sealed record Entry(Album? Album, DateTime At);

    public bool TryGet(string key, out Album? album, out bool stale)
    {
        album = null;
        stale = false;
        if (!_cache.TryGetValue("album-merge:" + key, out Entry? entry) || entry == null)
        {
            return false;
        }

        album = entry.Album;
        stale = DateTime.UtcNow - entry.At > (entry.Album == null ? MissTtl / 2 : Refresh);
        return true;
    }

    /// <summary>
    /// Resolves and stores the answer; callers asking for the same key meanwhile share it.
    /// </summary>
    public Task<Album?> ResolveAsync(string key, Func<Task<Album?>> resolve)
    {
        lock (_inFlight)
        {
            if (_inFlight.TryGetValue(key, out var running))
            {
                return running;
            }

            var task = Run(key, resolve);
            _inFlight[key] = task;
            return task;
        }
    }

    private async Task<Album?> Run(string key, Func<Task<Album?>> resolve)
    {
        await Task.Yield();
        try
        {
            var album = await resolve();
            _cache.Set("album-merge:" + key, new Entry(album, DateTime.UtcNow), new MemoryCacheEntryOptions()
                .SetSize(1)
                .SetAbsoluteExpiration(album == null ? MissTtl : MatchTtl));
            return album;
        }
        finally
        {
            lock (_inFlight)
            {
                _inFlight.Remove(key);
            }
        }
    }
}

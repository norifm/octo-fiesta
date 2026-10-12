using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;

namespace octo_fiesta.Services.Composite;

/// <summary>
/// Fans searches out to every configured provider and merges the results; lookups by id are
/// routed to the provider named in the id. A provider that throws or does not answer within
/// <see cref="ProviderTimeout"/> is logged and skipped, so one offline service never fails the query
/// against the others.
/// </summary>
public class CompositeMetadataService : IMusicMetadataService
{
    public IReadOnlyList<(string Key, IMusicMetadataService Service)> Providers { get; }
    private readonly ILogger<CompositeMetadataService> _logger;

    /// <summary>How long one provider gets to answer before it is skipped for that request.</summary>
    public TimeSpan ProviderTimeout { get; }

    public CompositeMetadataService(
        IReadOnlyList<(string Key, IMusicMetadataService Service)> providers,
        ILogger<CompositeMetadataService> logger,
        TimeSpan? providerTimeout = null)
    {
        Providers = providers;
        _logger = logger;
        ProviderTimeout = providerTimeout ?? TimeSpan.FromSeconds(20);
    }

    // Runs one provider call; on an exception or timeout logs it and returns the fallback.
    // Timed-out work cannot be cancelled until provider interfaces accept cancellation tokens.
    private async Task<T> Guard<T>(string provider, Func<Task<T>> call, T fallback)
    {
        try { return await call().WaitAsync(ProviderTimeout); }
        catch (TimeoutException)
        {
            _logger.LogWarning("Provider {Provider} did not answer within {Seconds}s, skipping it for this request", provider, ProviderTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Provider {Provider} failed, skipping it for this request", provider);
        }
        return fallback;
    }

    private IMusicMetadataService? For(string provider)
        => Providers.FirstOrDefault(p => p.Key.Equals(provider, StringComparison.OrdinalIgnoreCase)).Service;

    private async Task<List<List<T>>> FanOut<T>(Func<IMusicMetadataService, Task<List<T>>> call)
    {
        var tasks = Providers.Select(p => Guard(p.Key, () => call(p.Service), new List<T>()));
        return (await Task.WhenAll(tasks)).ToList();
    }

    // Round-robin so every provider is represented within the limit.
    private static List<T> Interleave<T>(List<List<T>> lists, int limit)
    {
        var result = new List<T>();
        for (var i = 0; result.Count < limit && lists.Any(l => i < l.Count); i++)
            foreach (var l in lists)
                if (i < l.Count && result.Count < limit) result.Add(l[i]);
        return result;
    }

    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20)
        => Interleave(await FanOut(s => s.SearchSongsAsync(query, limit)), limit);

    public async Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20)
        => Interleave(await FanOut(s => s.SearchAlbumsAsync(query, limit)), limit);

    public async Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20)
        => Interleave(await FanOut(s => s.SearchArtistsAsync(query, limit)), limit);

    public async Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20)
        => Interleave(await FanOut(s => s.SearchPlaylistsAsync(query, limit)), limit);

    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20)
    {
        var tasks = Providers.Select(p => Guard(p.Key, () => p.Service.SearchAllAsync(query, songLimit, albumLimit, artistLimit), new SearchResult()));
        var all = await Task.WhenAll(tasks);
        return new SearchResult
        {
            Songs = Interleave(all.Select(r => r.Songs).ToList(), songLimit),
            Albums = Interleave(all.Select(r => r.Albums).ToList(), albumLimit),
            Artists = Interleave(all.Select(r => r.Artists).ToList(), artistLimit)
        };
    }

    private Task<T> Route<T>(string provider, Func<IMusicMetadataService, Task<T>> call, T fallback)
        => For(provider) is { } service ? Guard(provider, () => call(service), fallback) : Task.FromResult(fallback);

    public Task<Song?> GetSongAsync(string externalProvider, string externalId)
        => Route(externalProvider, s => s.GetSongAsync(externalProvider, externalId), (Song?)null);

    public Task<Album?> GetAlbumAsync(string externalProvider, string externalId)
        => Route(externalProvider, s => s.GetAlbumAsync(externalProvider, externalId), (Album?)null);

    public Task<string?> GetAlbumCoverUrlAsync(string externalProvider, string externalId)
        => Route(externalProvider, s => s.GetAlbumCoverUrlAsync(externalProvider, externalId), (string?)null);

    public Task<Artist?> GetArtistAsync(string externalProvider, string externalId)
        => Route(externalProvider, s => s.GetArtistAsync(externalProvider, externalId), (Artist?)null);

    public Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId)
        => Route(externalProvider, s => s.GetArtistAlbumsAsync(externalProvider, externalId), new List<Album>());

    public Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId)
        => Route(externalProvider, s => s.GetPlaylistAsync(externalProvider, externalId), (ExternalPlaylist?)null);

    public Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId)
        => Route(externalProvider, s => s.GetPlaylistTracksAsync(externalProvider, externalId), new List<Song>());
}

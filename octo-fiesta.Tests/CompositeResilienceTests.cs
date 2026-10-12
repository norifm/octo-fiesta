using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Search;
using octo_fiesta.Services;
using octo_fiesta.Services.Composite;
using Xunit;

namespace octo_fiesta.Tests;

/// <summary>An offline provider must never fail or stall a request that other providers can answer.</summary>
public class CompositeResilienceTests
{
    private static Song Track(string provider, string id) => new() { Title = id, ExternalProvider = provider, ExternalId = id };

    private static Mock<IMusicMetadataService> Working(string provider)
    {
        var m = new Mock<IMusicMetadataService>();
        m.Setup(x => x.SearchSongsAsync(It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync([Track(provider, "1")]);
        m.Setup(x => x.SearchAllAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new SearchResult { Songs = [Track(provider, "1")] });
        return m;
    }

    private static CompositeMetadataService Composite(TimeSpan timeout, params (string, IMusicMetadataService)[] providers)
        => new(providers, NullLogger<CompositeMetadataService>.Instance, timeout);

    [Fact]
    public async Task Search_WhenOneProviderThrows_ReturnsTheOthersResults()
    {
        var broken = new Mock<IMusicMetadataService>();
        broken.Setup(x => x.SearchSongsAsync(It.IsAny<string>(), It.IsAny<int>())).ThrowsAsync(new HttpRequestException("offline"));

        var result = await Composite(TimeSpan.FromSeconds(5), ("apple", broken.Object), ("gdstudio", Working("gdstudio").Object))
            .SearchSongsAsync("q");

        Assert.Equal("gdstudio", Assert.Single(result).ExternalProvider);
    }

    [Fact]
    public async Task Search_WhenOneProviderHangs_ReturnsTheOthersAfterTheTimeout()
    {
        var hung = new Mock<IMusicMetadataService>();
        hung.Setup(x => x.SearchSongsAsync(It.IsAny<string>(), It.IsAny<int>())).Returns(new TaskCompletionSource<List<Song>>().Task);

        var started = DateTime.UtcNow;
        var result = await Composite(TimeSpan.FromMilliseconds(300), ("apple", hung.Object), ("gdstudio", Working("gdstudio").Object))
            .SearchSongsAsync("q");

        Assert.Equal("gdstudio", Assert.Single(result).ExternalProvider);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task SearchAll_WhenOneProviderThrows_ReturnsTheOthersResults()
    {
        var broken = new Mock<IMusicMetadataService>();
        broken.Setup(x => x.SearchAllAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await Composite(TimeSpan.FromSeconds(5), ("apple", broken.Object), ("gdstudio", Working("gdstudio").Object))
            .SearchAllAsync("q");

        Assert.Equal("gdstudio", Assert.Single(result.Songs).ExternalProvider);
    }

    [Fact]
    public async Task GetAlbum_WhenItsProviderIsOffline_ReturnsNullInsteadOfThrowing()
    {
        var broken = new Mock<IMusicMetadataService>();
        broken.Setup(x => x.GetAlbumAsync("apple", "1")).ThrowsAsync(new HttpRequestException("offline"));

        var album = await Composite(TimeSpan.FromSeconds(5), ("apple", broken.Object)).GetAlbumAsync("apple", "1");

        Assert.Null(album);
    }

    [Fact]
    public async Task AlbumCover_RoutesToTheLightweightLookup()
    {
        var provider = new Mock<IMusicMetadataService>();
        provider.Setup(p => p.GetAlbumCoverUrlAsync("qobuz", "1")).ReturnsAsync("https://example.test/cover");
        var composite = Composite(TimeSpan.FromSeconds(1), ("qobuz", provider.Object));
        Assert.Equal("https://example.test/cover", await composite.GetAlbumCoverUrlAsync("qobuz", "1"));
        provider.Verify(p => p.GetAlbumAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Searches_InterleaveAndRespectLimit_AndDownloadsRouteByProvider()
    {
        var first = new Mock<IMusicMetadataService>();
        var second = new Mock<IMusicMetadataService>();
        first.Setup(p => p.SearchSongsAsync("q", 3)).ReturnsAsync([Track("deezer", "1"), Track("deezer", "2")]);
        second.Setup(p => p.SearchSongsAsync("q", 3)).ReturnsAsync([Track("qobuz", "1"), Track("qobuz", "2")]);
        var composite = Composite(TimeSpan.FromSeconds(1), ("deezer", first.Object), ("qobuz", second.Object));
        Assert.Equal(["deezer", "qobuz", "deezer"], (await composite.SearchSongsAsync("q", 3)).Select(s => s.ExternalProvider!).ToArray());
        var deezer = new Mock<IDownloadService>();
        var qobuz = new Mock<IDownloadService>();
        qobuz.Setup(d => d.DownloadSongAsync("qobuz", "1", default)).ReturnsAsync("qobuz-file");
        var downloads = new CompositeDownloadService([("deezer", deezer.Object), ("qobuz", qobuz.Object)]);
        Assert.Equal("qobuz-file", await downloads.DownloadSongAsync("qobuz", "1"));
        deezer.Verify(d => d.DownloadSongAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IsAvailable_WhenTheFirstProviderThrows_StillChecksTheOthers()
    {
        var broken = new Mock<octo_fiesta.Services.IDownloadService>();
        broken.Setup(x => x.IsAvailableAsync()).ThrowsAsync(new HttpRequestException("offline"));
        var ok = new Mock<octo_fiesta.Services.IDownloadService>();
        ok.Setup(x => x.IsAvailableAsync()).ReturnsAsync(true);

        var composite = new CompositeDownloadService([("apple", broken.Object), ("gdstudio", ok.Object)]);

        Assert.True(await composite.IsAvailableAsync());
    }
}

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.GDStudio;

namespace octo_fiesta.Tests;

public class GDStudioMetadataRegressionTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }
    private static HttpResponseMessage Tracks(int count, string artist = "Artist") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(Enumerable.Range(1, count).Select(i => new
        { id = i.ToString(), name = $"Song {i}", artist = new[] { artist }, album = "Album", pic_id = (string?)null })))
    };
    private static GDStudioMetadataService Service(HttpMessageHandler handler, string source = "netease")
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(GDStudioHttpClientConfiguration.ClientName)).Returns(new HttpClient(handler));
        return new GDStudioMetadataService(factory.Object, Options.Create(new GDStudioSettings { Source = source }),
            NullLogger<GDStudioMetadataService>.Instance);
    }

    [Fact]
    public async Task SongTitleQuery_IsNeverAddedToAppleArtistTags()
    {
        var service = Service(new Handler(_ => Task.FromResult(Tracks(1))), "apple");
        var song = Assert.Single(await service.SearchSongsAsync("Song 1"));
        Assert.Equal("Artist", song.Artist);
        Assert.Single(song.Artists);
        Assert.Equal("Artist", song.AlbumArtist);
    }

    [Fact]
    public async Task AlbumOpening_FetchesAdditionalTracksBeyondSearchPreview()
    {
        var calls = 0;
        var service = Service(new Handler(request =>
        {
            calls++;
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["name"];
            return Task.FromResult(Tracks(query == "preview" ? 1 : 3));
        }));
        var preview = Assert.Single(await service.SearchAlbumsAsync("preview"));
        Assert.Equal(1, preview.SongCount);
        var album = await service.GetAlbumAsync("gdstudio", preview.ExternalId!);
        Assert.Equal(3, album!.Songs.Count);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task MultiSourceSongSearch_RespectsTheRequestedLimit()
    {
        var service = Service(new Handler(_ => Task.FromResult(Tracks(5))), "netease,joox");
        var songs = await service.SearchSongsAsync("query", 3);
        Assert.Equal(3, songs.Count);
        Assert.Equal(["netease~1", "joox~1", "netease~2"], songs.Select(s => s.ExternalId!).ToArray());
    }

    [Fact]
    public async Task MultiSourceAlbum_UsesOneSourceInsteadOfDuplicateCopies()
    {
        var service = Service(new Handler(_ => Task.FromResult(Tracks(2))), "netease,joox");
        var preview = Assert.Single(await service.SearchAlbumsAsync("Album"));
        var album = await service.GetAlbumAsync("gdstudio", preview.ExternalId!);
        Assert.Equal(2, album!.Songs.Count);
        Assert.All(album.Songs, song => Assert.StartsWith("netease~", song.ExternalId));
    }

    [Fact]
    public async Task ConcurrentIdenticalQueries_ShareAnInFlightRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var service = Service(new Handler(async _ =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task;
            return Tracks(1);
        }));
        var first = service.SearchSongsAsync("same");
        await entered.Task;
        var rest = Enumerable.Range(0, 20).Select(_ => service.SearchSongsAsync("same")).ToArray();
        release.SetResult();
        await Task.WhenAll(rest.Append(first));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task UnknownSongId_ReturnsNullInsteadOfInventedMetadata()
    {
        var service = Service(new Handler(_ => throw new InvalidOperationException("No request needed")));
        Assert.Null(await service.GetSongAsync("gdstudio", "netease~unknown"));
    }
}

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.GDStudio;
using Xunit;

namespace octo_fiesta.Tests;

/// <summary>GD Studio answers 503/429 once it is hit too often; a burst must not lose the whole query.</summary>
public class GDStudioRateLimitTests
{
    private sealed class StubHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(Interlocked.Increment(ref Calls)));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private const string OneTrack = """[{"id":"1","name":"Song","artist":["A"],"album":"Al","pic_id":null}]""";

    private static GDStudioMetadataService Service(StubHandler handler, string source = "netease")
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(GDStudioHttpClientConfiguration.ClientName)).Returns(new HttpClient(handler));
        return new GDStudioMetadataService(factory.Object,
            Options.Create(new GDStudioSettings { Source = source, TimeoutSeconds = 5 }),
            NullLogger<GDStudioMetadataService>.Instance);
    }

    [Fact]
    public async Task A503IsRetriedOnce()
    {
        var handler = new StubHandler(n => n == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(OneTrack));

        var songs = await Service(handler).SearchSongsAsync("q");

        Assert.Single(songs);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task IdenticalQueriesShareOneRequest()
    {
        var handler = new StubHandler(_ => Json(OneTrack));
        var service = Service(handler);

        await Task.WhenAll(service.SearchSongsAsync("q"), service.SearchSongsAsync("q"));
        await service.SearchSongsAsync("q");

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AFailedQueryIsNotRemembered()
    {
        var handler = new StubHandler(n => n <= 2 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(OneTrack));
        var service = Service(handler);

        Assert.Empty(await service.SearchSongsAsync("q")); // 503, retry 503 -> skipped, no exception
        Assert.Single(await service.SearchSongsAsync("q")); // asks again instead of replaying the failure
    }

    [Fact]
    public async Task OneSourceFailingStillReturnsTheOther()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OneTrack) });
        // both sources answer; each song id carries its source
        var songs = await Service(handler, "netease,joox").SearchSongsAsync("q");

        Assert.Equal(2, songs.Count);
        Assert.Equal(["netease~1", "joox~1"], songs.Select(s => s.ExternalId!).OrderByDescending(x => x).ToArray());
    }

    // ---- artist page: one keyword search (paged) instead of one lookup per album ----

    private static string Tracks(string artist, string album, int from, int n)
        => "[" + string.Join(",", Enumerable.Range(from, n).Select(i =>
            $$"""{"id":"{{i}}","name":"S{{i}}","artist":["{{artist}}"],"album":"{{album}}","pic_id":null}""")) + "]";

    private static string PageOf(HttpRequestMessage r)
        => System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query)["pages"] ?? "1"; // page 1 sends no pages param

    private sealed class PagedHandler(Func<string, HttpResponseMessage> byPage) : HttpMessageHandler
    {
        public readonly List<string> Pages = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Pages) Pages.Add(PageOf(request));
            return Task.FromResult(byPage(PageOf(request)));
        }
    }

    private static GDStudioMetadataService ServiceFor(HttpMessageHandler handler, string source = "netease")
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(GDStudioHttpClientConfiguration.ClientName)).Returns(new HttpClient(handler));
        return new GDStudioMetadataService(factory.Object,
            Options.Create(new GDStudioSettings { Source = source, TimeoutSeconds = 5 }),
            NullLogger<GDStudioMetadataService>.Instance);
    }

    private static string Id(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public async Task ArtistAlbums_PagesThroughTheKeywordSearch_AlbumOpeningFetchesItsOwnListing()
    {
        // page 1 is full (99) and mixes in another artist; once it is full, pages 2 and 3 go out together
        var handler = new PagedHandler(page => Json(page == "1"
            ? Tracks("A", "Al", 1, 60).TrimEnd(']') + "," + Tracks("B", "Other", 100, 39).TrimStart('[')
            : page == "2" ? Tracks("A", "Al", 200, 2) : "[]"));
        var service = ServiceFor(handler);

        var albums = await service.GetArtistAlbumsAsync("gdstudio", Id("A"));

        var album = Assert.Single(albums);
        Assert.Equal("Al", album.Title);
        Assert.Equal(62, album.SongCount);
        Assert.Equal(["1", "2"], handler.Pages.OrderBy(x => x).ToArray());

        var opened = await service.GetAlbumAsync("gdstudio", album.ExternalId!);

        Assert.Equal(62, opened!.Songs.Count);
        Assert.Equal(4, handler.Pages.Count); // album search uses separate cached query keys
        await service.GetAlbumAsync("gdstudio", album.ExternalId!);
        Assert.Equal(4, handler.Pages.Count); // repeating the album lookup uses the cache
    }

    [Fact]
    public async Task ArtistAlbums_WhenALaterPageFails_ReturnsWhatArrived()
    {
        var handler = new PagedHandler(page => page == "1"
            ? Json(Tracks("A", "Al", 1, 99))
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var albums = await ServiceFor(handler).GetArtistAlbumsAsync("gdstudio", Id("A"));

        Assert.Equal(99, Assert.Single(albums).SongCount);
    }

    // ---- a source that answers count > 50 with an empty list (apple) ----

    private sealed class CountingHandler(int max) : HttpMessageHandler
    {
        public readonly List<int> Counts = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var count = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["count"]!);
            lock (Counts) Counts.Add(count);
            return Task.FromResult(Json(count > max ? "[]" : OneTrack));
        }
    }

    [Fact]
    public async Task EmptyAnswerToABigCount_IsAskedAgainWithFifty_AndThatSourceStaysCapped()
    {
        var handler = new CountingHandler(max: 50);
        var service = ServiceFor(handler);

        var first = await service.SearchSongsAsync("q", 99);
        var second = await service.SearchSongsAsync("other", 99);

        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal([99, 50, 50], handler.Counts); // second query went straight to 50
    }

    [Fact]
    public async Task GenuinelyEmptyResult_DoesNotCapTheSource()
    {
        var handler = new CountingHandler(max: 0); // always empty, whatever the count
        var service = ServiceFor(handler);

        Assert.Empty(await service.SearchSongsAsync("q", 99));
        Assert.Empty(await service.SearchSongsAsync("other", 99));

        Assert.Equal([99, 50, 99, 50], handler.Counts); // retried once each, never capped
    }

    [Fact]
    public async Task KnownLimit_IsUsedFromTheFirstRequest_WithoutProbing()
    {
        var handler = new CountingHandler(max: 50);

        var songs = await ServiceFor(handler, "apple").SearchSongsAsync("q", 99);

        Assert.Single(songs);
        Assert.Equal([50], handler.Counts); // apple is in the map: no failed 99 first, one request
    }

    [Fact]
    public async Task UnknownSourceAtTheApiMax_KeepsAskingForTheApiMax()
    {
        var handler = new CountingHandler(max: 99);
        var service = ServiceFor(handler, "netease");

        await service.SearchSongsAsync("q", 99);
        await service.SearchSongsAsync("other", 99);

        Assert.Equal([99, 99], handler.Counts);
    }
}

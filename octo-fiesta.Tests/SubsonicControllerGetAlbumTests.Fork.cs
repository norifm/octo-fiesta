using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using octo_fiesta.Controllers;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Subsonic;

namespace octo_fiesta.Tests;

// Fork album-merge tests, kept apart so upstream test edits merge cleanly.
public partial class SubsonicControllerGetAlbumTests
{
    private const string NavidromeMoonSafariJson =
        "{\"subsonic-response\":{\"status\":\"ok\",\"album\":{\"id\":\"local-album-3\",\"name\":\"Moon Safari\"," +
        "\"artist\":\"Air\",\"songCount\":2,\"duration\":500,\"song\":[" +
        "{\"id\":\"s1\",\"title\":\"Sexy Boy\",\"discNumber\":1,\"track\":2,\"duration\":300}," +
        "{\"id\":\"s2\",\"title\":\"All I Need\",\"discNumber\":1,\"track\":3,\"duration\":200}]}}}";

    private static Album Candidate(string id, string title, string artist = "Air") => new()
    {
        Id = $"ext-deezer-album-{id}",
        Title = title,
        Artist = artist,
        ExternalProvider = "deezer",
        ExternalId = id
    };

    private static Song RemoteSong(string title, int track) => new()
    {
        Title = title,
        Track = track,
        DiscNumber = 1,
        Duration = 200,
        ExternalProvider = "deezer",
        ExternalId = $"r{track}"
    };

    private static Mock<IMusicMetadataService> MoonSafariProvider(params Album[] candidates)
    {
        var mock = new Mock<IMusicMetadataService>();
        mock.Setup(x => x.SearchAlbumsAsync("Air Moon Safari", It.IsAny<int>())).ReturnsAsync(candidates.ToList());
        return mock;
    }

    [Theory]
    [InlineData("Moon Safari: Remixes, Rarities and Radio Sessions - EP")]
    [InlineData("Moon Safari: Live from the Kennedy Center")]
    [InlineData("Moon Safari (Anyma Remix) - Single")]
    public async Task GetAlbum_WhenCandidateIsAnotherReleaseWithTheSameTitleStart_KeepsItsSongsOut(string title)
    {
        var provider = MoonSafariProvider(Candidate("1", title));
        provider.Setup(x => x.GetAlbumAsync("deezer", "1")).ReturnsAsync(new Album
        {
            Songs = new List<Song> { RemoteSong("Sexy Boy (Live)", 1) }
        });

        var result = await CreateController(provider, asXml: false, navidromeJson: NavidromeMoonSafariJson).GetAlbum();

        Assert.Equal(new[] { "Sexy Boy", "All I Need" }, GetMergedSongTitles(result));
        provider.Verify(x => x.GetAlbumAsync("deezer", "1"), Times.Never);
    }

    [Fact]
    public async Task GetAlbum_WhenAlbumIsOnlyPartlyOwned_AppendsTheMissingTracksOfTheSameAlbum()
    {
        var provider = MoonSafariProvider(Candidate("2", "Moon Safari"));
        provider.Setup(x => x.GetAlbumAsync("deezer", "2")).ReturnsAsync(new Album
        {
            Songs = new List<Song> { RemoteSong("La femme d'argent", 1), RemoteSong("Sexy Boy", 2), RemoteSong("All I Need", 3) }
        });

        var result = await CreateController(provider, asXml: false, navidromeJson: NavidromeMoonSafariJson).GetAlbum();

        Assert.Equal(new[] { "La femme d'argent", "Sexy Boy", "All I Need" }, GetMergedSongTitles(result));
    }

    [Fact]
    public async Task GetAlbum_WhenSameTitleCandidateSharesNoTracks_KeepsItsSongsOut()
    {
        var provider = MoonSafariProvider(Candidate("3", "Moon Safari"));
        provider.Setup(x => x.GetAlbumAsync("deezer", "3")).ReturnsAsync(new Album
        {
            Songs = new List<Song> { RemoteSong("Something Else", 1) }
        });

        var result = await CreateController(provider, asXml: false, navidromeJson: NavidromeMoonSafariJson).GetAlbum();

        Assert.Equal(new[] { "Sexy Boy", "All I Need" }, GetMergedSongTitles(result));
    }

    [Fact]
    public async Task GetAlbum_WithCache_AsksTheProviderOnceAndMergesOnTheNextView()
    {
        var provider = MoonSafariProvider(Candidate("2", "Moon Safari"));
        provider.Setup(x => x.GetAlbumAsync("deezer", "2")).ReturnsAsync(new Album
        {
            Songs = new List<Song> { RemoteSong("La femme d'argent", 1), RemoteSong("Sexy Boy", 2) }
        });
        var cache = new ExternalAlbumMergeCache(new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 16 }));

        var first = await CreateController(provider, asXml: false, navidromeJson: NavidromeMoonSafariJson, mergeCache: cache).GetAlbum();
        var second = await CreateController(provider, asXml: false, navidromeJson: NavidromeMoonSafariJson, mergeCache: cache).GetAlbum();

        Assert.Equal(new[] { "La femme d'argent", "Sexy Boy", "All I Need" }, GetMergedSongTitles(first));
        Assert.Equal(new[] { "La femme d'argent", "Sexy Boy", "All I Need" }, GetMergedSongTitles(second));
        provider.Verify(x => x.SearchAlbumsAsync("Air Moon Safari", It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task GetAlbum_WithCache_DoesNotWaitForASlowProviderOnLaterViews()
    {
        var release = new TaskCompletionSource();
        var provider = MoonSafariProvider();
        provider.Setup(x => x.SearchAlbumsAsync("Air Moon Safari", It.IsAny<int>()))
            .Returns(async () => { await release.Task; return new List<Album>(); });
        var cache = new ExternalAlbumMergeCache(new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 16 }));

        var viewing = CreateController(provider, asXml: false, navidromeJson: NavidromeMoonSafariJson, mergeCache: cache).GetAlbum();
        release.SetResult();
        await viewing;
        var started = DateTime.UtcNow;
        await CreateController(provider, asXml: false, navidromeJson: NavidromeMoonSafariJson, mergeCache: cache).GetAlbum();

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
        provider.Verify(x => x.SearchAlbumsAsync("Air Moon Safari", It.IsAny<int>()), Times.Once);
    }

    private static Mock<IMusicMetadataService> RemoteKammthaarProvider()
    {
        var provider = CreateMetadataService();
        provider.Setup(x => x.GetAlbumAsync("deezer", "7")).ReturnsAsync(new Album
        {
            Id = "ext-deezer-album-7",
            Title = "Kammthaar",
            Artist = "Ultra Vomit",
            Songs = new List<Song>
            {
                new Song { Title = "Kammthaar", Track = 1, DiscNumber = 1, Duration = 154, ExternalProvider = "deezer", ExternalId = "6" },
                new Song { Title = "Kammthaar (Live)", Track = 2, DiscNumber = 1, Duration = 200, ExternalProvider = "deezer", ExternalId = "8" }
            }
        });
        return provider;
    }

    private static Action<Mock<ILocalLibraryService>> RemoteAlbumLibrary(string? mappedLocalId) => library =>
    {
        library.Setup(x => x.ParseExternalId("ext-deezer-album-7")).Returns((true, "deezer", "album", "7"));
        library.Setup(x => x.GetMappingForExternalSongAsync("deezer", "6")).ReturnsAsync(
            mappedLocalId == null ? null : new LocalSongMapping { ExternalProvider = "deezer", ExternalId = "6", LocalSubsonicId = mappedLocalId });
    };

    private static string LibrarySongJson(string albumName) =>
        "{\"subsonic-response\":{\"status\":\"ok\",\"song\":{\"id\":\"local-song-1\",\"album\":\"" + albumName + "\",\"albumId\":\"local-album-1\"}}}";

    [Fact]
    public async Task GetAlbum_WhenRemoteAlbumIsOwnedByNow_ServesTheLibraryAlbum()
    {
        var controller = CreateController(
            RemoteKammthaarProvider(), asXml: false, albumId: "ext-deezer-album-7",
            songJson: LibrarySongJson("Kammthaar"), configureLibrary: RemoteAlbumLibrary("local-song-1"));

        var result = await controller.GetAlbum();

        var jsonResult = Assert.IsType<JsonResult>(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(jsonResult.Value));
        var songs = doc.RootElement.GetProperty("subsonic-response").GetProperty("album").GetProperty("song").EnumerateArray().ToList();
        Assert.Equal("local-song-1", songs[0].GetProperty("id").GetString());
        Assert.Equal(new[] { "Kammthaar", "Kammthaar (Live)" }, songs.Select(s => s.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task GetAlbum_WhenNothingOfRemoteAlbumIsMapped_StaysRemote()
    {
        var controller = CreateController(
            RemoteKammthaarProvider(), asXml: false, albumId: "ext-deezer-album-7",
            songJson: LibrarySongJson("Kammthaar"), configureLibrary: RemoteAlbumLibrary(null));

        var result = await controller.GetAlbum();

        Assert.Equal(new[] { "Kammthaar", "Kammthaar (Live)" }, GetMergedSongTitles(result));
        var jsonResult = Assert.IsType<JsonResult>(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(jsonResult.Value));
        Assert.StartsWith("ext-deezer-song", doc.RootElement.GetProperty("subsonic-response").GetProperty("album").GetProperty("song")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetAlbum_WhenMappedTrackLivesInAnotherAlbum_StaysRemote()
    {
        var controller = CreateController(
            RemoteKammthaarProvider(), asXml: false, albumId: "ext-deezer-album-7",
            songJson: LibrarySongJson("Another Album"), configureLibrary: RemoteAlbumLibrary("local-song-1"));

        var result = await controller.GetAlbum();

        var jsonResult = Assert.IsType<JsonResult>(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(jsonResult.Value));
        Assert.StartsWith("ext-deezer-song", doc.RootElement.GetProperty("subsonic-response").GetProperty("album").GetProperty("song")[0].GetProperty("id").GetString());
    }
}

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.GDStudio;
using octo_fiesta.Services.Local;
using Xunit;

namespace octo_fiesta.Tests;

public class GDStudioAppleSourceTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static string Id(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public async Task AppleSource_WhenQueriedInChinese_UsesExplicitAliasAndPreservesArtist()
    {
        // Upstream Apple Music returns Romanized artist (Jay Chou) when queried with Chinese name (周杰伦)
        var appleResponse = """
        [
            {"id":"1721450037","name":"Qi-Li-Xiang","artist":["Jay Chou"],"album":"Common Jasmine Orange","pic_id":null},
            {"id":"1721464906","name":"Sunny Day","artist":["Jay Chou"],"album":"Yeh, Hwei-Mei","pic_id":null}
        ]
        """;

        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.Query.Contains("source=apple"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(appleResponse) };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        });

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(GDStudioHttpClientConfiguration.ClientName)).Returns(new HttpClient(handler));

        var service = new GDStudioMetadataService(factory.Object,
            Options.Create(new GDStudioSettings { Source = "apple", TimeoutSeconds = 5, ArtistAliases = new() { ["周杰伦"] = ["Jay Chou"] } }),
            NullLogger<GDStudioMetadataService>.Instance);

        // 1. GetArtistAlbumsAsync for Chinese artist name
        var albums = await service.GetArtistAlbumsAsync("gdstudio", Id("周杰伦"));

        // Both albums should be kept and not filtered out by strict artist equality
        Assert.Equal(2, albums.Count);
        Assert.Contains(albums, a => a.Title == "Common Jasmine Orange");
        Assert.Contains(albums, a => a.Title == "Yeh, Hwei-Mei");

        // 2. A normal query never changes the upstream artist tags.
        var songs = await service.SearchSongsAsync("周杰伦");
        Assert.Equal(2, songs.Count);
        Assert.DoesNotContain("周杰伦", songs[0].Artist);
        Assert.Contains("Jay Chou", songs[0].Artist);
    }

    [Fact]
    public async Task DownloadService_ResolvesRelativeUrlsAgainstSiteOrigin()
    {
        HttpRequestMessage? mediaRequest = null;
        var handler = new StubHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (uri.Contains("types=url"))
            {
                // Return relative URL path as GDStudio does for Apple
                var json = JsonSerializer.Serialize(new { url = "cache/apple_1721450037.256.m4a", br = 256 });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            }

            // Media file download
            mediaRequest = req;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70]) // fake m4a header
            };
            return response;
        });

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(GDStudioHttpClientConfiguration.ClientName)).Returns(new HttpClient(handler));

        var settings = Options.Create(new GDStudioSettings
        {
            Api = "https://music-api.gdstudio.xyz/api.php",
            Source = "apple"
        });

        var localLibrary = new Mock<ILocalLibraryService>();
        var metadata = new Mock<IMusicMetadataService>();
        metadata.Setup(m => m.GetSongAsync("gdstudio", "apple~1721450037"))
            .ReturnsAsync(new Song { Title = "Qi-Li-Xiang", Artist = "Jay Chou, 周杰伦", Album = "Common Jasmine Orange" });
        var serviceProvider = new Mock<IServiceProvider>();
        var testDownloadPath = Path.Combine(Path.GetTempPath(), "octo-fiesta-gdstudio-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(testDownloadPath);

        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Library:DownloadPath"] = testDownloadPath
                })
                .Build();

            var downloadService = new GDStudioDownloadService(
                factory.Object,
                config,
                localLibrary.Object,
                metadata.Object,
                Options.Create(new SubsonicSettings()),
                settings,
                serviceProvider.Object,
                NullLogger<GDStudioDownloadService>.Instance);

            var result = await downloadService.DownloadAndStreamAsync("gdstudio", "apple~1721450037", CancellationToken.None);

            Assert.NotNull(result.Stream);
            Assert.Equal(".m4a", Path.GetExtension(result.FilePath));
            Assert.NotNull(mediaRequest);
            // Verify media request went to music.gdstudio.xyz (site origin), NOT music-api.
            Assert.Equal("https://music.gdstudio.xyz/cache/apple_1721450037.256.m4a", mediaRequest.RequestUri!.ToString());
        }
        finally
        {
            if (Directory.Exists(testDownloadPath))
            {
                try { Directory.Delete(testDownloadPath, true); } catch { }
            }
        }
    }
}

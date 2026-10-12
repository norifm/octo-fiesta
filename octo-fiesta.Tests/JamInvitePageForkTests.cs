using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using octo_fiesta.Services.Remote;

namespace octo_fiesta.Tests;

public class JamInvitePageForkTests : IClassFixture<JamInvitePageForkTests.App>
{
    public sealed class App : WebApplicationFactory<RemoteHub>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Subsonic:Url", "http://127.0.0.1:9");
            builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        }
    }

    private readonly App _app;

    public JamInvitePageForkTests(App app) => _app = app;

    [Fact]
    public async Task TheInvitePageOpensWithoutSigningIn()
    {
        var response = await _app.CreateClient().GetAsync("/nori/jam");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var page = await response.Content.ReadAsStringAsync();
        Assert.Contains("Open in nori", page);
        Assert.Contains("package=dev.nori.music", page);
    }
}

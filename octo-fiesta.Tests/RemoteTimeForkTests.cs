using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using octo_fiesta.Services.Remote;

namespace octo_fiesta.Tests;

public class RemoteTimeForkTests : IClassFixture<RemoteTimeForkTests.App>
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

    public RemoteTimeForkTests(App app) => _app = app;

    private async Task<JsonElement> Time(HttpClient client, long t1)
    {
        var response = await client.GetAsync($"/nori/time?t1={t1}");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task TheServerTellsItsTimeWithoutSigningIn()
    {
        var client = _app.CreateClient();
        var first = await Time(client, 42);
        var second = await Time(client, 43);

        Assert.Equal("clock", first.GetProperty("t").GetString());
        Assert.Equal(42, first.GetProperty("t1").GetInt64());
        var (t2, t3) = (first.GetProperty("t2").GetInt64(), first.GetProperty("t3").GetInt64());
        Assert.InRange(t3 - t2, 0, 50_000);
        Assert.True(second.GetProperty("t2").GetInt64() >= t3, "one clock, running on");
    }
}

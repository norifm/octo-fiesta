using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.AppleMusic;
using octo_fiesta.Services.Composite;
using octo_fiesta.Services.Local;

namespace octo_fiesta.Tests;

public class ProviderFeatureTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Theory]
    [InlineData("Deezer")]
    [InlineData("Qobuz")]
    [InlineData("Tidal")]
    [InlineData("AppleMusic")]
    public void ExistingSingleProvider_UsesLegacyRegistration(string provider)
    {
        var config = Config(("Subsonic:MusicService", provider));
        ProviderFeature.NormalizeConfiguration(config);
        var settings = config.GetSection("Subsonic").Get<SubsonicSettings>()!;
        Assert.Equal(provider, settings.MusicService.ToString());
        Assert.False(ProviderFeature.TryRegister(new ServiceCollection(), config));
    }

    [Theory]
    [InlineData("Deezer,Typo")]
    [InlineData("999")]
    [InlineData("0")]
    public void InvalidProviderNames_FailClearly(string value)
        => Assert.Throws<InvalidOperationException>(() => ProviderFeature.NormalizeConfiguration(Config(("Subsonic:MusicService", value))));

    [Fact]
    public void MultipleProviders_WithoutAnyCredentials_FailInsteadOfStartingEmpty()
    {
        var config = Config(("Subsonic:MusicService", "Deezer,Qobuz"));
        ProviderFeature.NormalizeConfiguration(config);
        Assert.Throws<InvalidOperationException>(() => ProviderFeature.TryRegister(new ServiceCollection(), config));
    }

    [Fact]
    public void GDStudio_WithConfiguredAppleMusic_PreservesAutomaticAppleMusicAndRoutesDownloads()
    {
        var root = Path.Combine(Path.GetTempPath(), "octo-provider-" + Guid.NewGuid());
        try
        {
            var config = Config(("Subsonic:MusicService", "GDStudio"),
                ("Subsonic:EnableExternalPlaylists", "false"), ("Library:DownloadPath", root),
                ("AppleMusic:AlacarteUrl", "http://localhost:7373"), ("AppleMusic:ApiToken", "test"));
            var services = new ServiceCollection();
            services.AddLogging(); services.AddHttpClient();
            services.AddSingleton(config);
            services.AddSingleton<ILocalLibraryService>(new Mock<ILocalLibraryService>().Object);
            services.Configure<SubsonicSettings>(config.GetSection("Subsonic"));
            services.Configure<AppleMusicSettings>(config.GetSection("AppleMusic"));
            ProviderFeature.Configure(services, config);
            Assert.True(ProviderFeature.TryRegister(services, config));
            using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
            var metadata = Assert.IsType<CompositeMetadataService>(sp.GetRequiredService<IMusicMetadataService>());
            var download = Assert.IsType<CompositeDownloadService>(sp.GetRequiredService<IDownloadService>());
            Assert.Equal(["gdstudio", "applemusic"], metadata.Providers.Select(p => p.Key).ToArray());
            Assert.Equal(["gdstudio", "applemusic"], download.Providers.Select(p => p.Key).ToArray());
            Assert.Contains(sp.GetServices<IMusicMetadataService>(), s => s is AppleMusicMetadataService);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void CanonicalGDStudioSettings_TakePrecedenceOverAliases()
    {
        var config = Config(("GDStudio:Source", "apple"), ("GDSTUDIO_SOURCE", "netease"));
        ProviderFeature.NormalizeConfiguration(config);
        Assert.Equal("apple", config["GDStudio:Source"]);
    }
}

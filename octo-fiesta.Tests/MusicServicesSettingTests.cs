using Microsoft.Extensions.Configuration;
using octo_fiesta.Models.Settings;
using Xunit;

namespace octo_fiesta.Tests;

public class MusicServicesSettingTests
{
    [Fact]
    public void ConfigKey_MusicService_BindsTheRawList()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Subsonic:MusicService"] = "Deezer,Qobuz" })
            .Build();

        octo_fiesta.Services.Composite.ProviderFeature.NormalizeConfiguration(config);
        var settings = config.GetSection("Subsonic").Get<SubsonicSettings>()!;

        Assert.Equal("Deezer,Qobuz", settings.MusicServices);
        Assert.Equal(MusicService.Deezer, settings.MusicService);
    }

    [Fact]
    public void Default_IsDeezer()
    {
        Assert.Equal([MusicService.Deezer], SubsonicSettings.ParseMusicServices(new SubsonicSettings().MusicServices, out _));
    }

    [Theory]
    [InlineData("Deezer,Qobuz,AppleMusic")]
    [InlineData(" deezer ; QOBUZ | applemusic ")]
    [InlineData("Deezer,Qobuz,AppleMusic,Deezer")]
    public void Parse_HandlesSeparatorsCaseSpacesAndDuplicates(string value)
    {
        var result = SubsonicSettings.ParseMusicServices(value, out var unknown);

        Assert.Equal([MusicService.Deezer, MusicService.Qobuz, MusicService.AppleMusic], result);
        Assert.Empty(unknown);
    }

    [Fact]
    public void Parse_ReportsUnknownEntries()
    {
        var result = SubsonicSettings.ParseMusicServices("Qobuz,Foo", out var unknown);

        Assert.Equal([MusicService.Qobuz], result);
        Assert.Equal(["Foo"], unknown);
    }

    [Fact]
    public void Parse_BlankMeansDeezer()
    {
        Assert.Equal([MusicService.Deezer], SubsonicSettings.ParseMusicServices("  ", out _));
    }
}

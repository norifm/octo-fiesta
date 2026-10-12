using octo_fiesta.Models.Domain;
using octo_fiesta.Services.Subsonic;
using Xunit;

namespace octo_fiesta.Tests;

/// <summary>An external song without a file has no real format yet: its suffix names where it comes from.</summary>
public class ExternalSongSuffixTests
{
    private static SubsonicResponseBuilder Builder() => new(Microsoft.Extensions.Options.Options.Create(
        new octo_fiesta.Models.Settings.SubsonicSettings { ShowExternalProviderInSuffix = true }));

    [Fact]
    public void Default_PreservesTheLegacyRemoteSuffix()
        => Assert.Equal("Remote", new SubsonicResponseBuilder().ConvertSongToJson(External("deezer", "1"))["suffix"]);

    private static string Suffix(Song song) => (string)Builder().ConvertSongToJson(song)["suffix"];

    private static Song External(string provider, string id) => new() { Title = "t", ExternalProvider = provider, ExternalId = id };

    [Theory]
    [InlineData("applemusic", "1638697115", "AppleMusic")]
    [InlineData("deezer", "42", "Deezer")]
    [InlineData("gdstudio", "apple~1020406366", "GDStudio-Apple")]
    [InlineData("gdstudio", "netease~167827", "GDStudio-Netease")]
    public void NamesTheOrigin(string provider, string id, string expected)
        => Assert.Equal(expected, Suffix(External(provider, id)));

    [Fact]
    public void ADownloadedFileKeepsItsRealFormat()
    {
        var song = External("gdstudio", "apple~1");
        song.LocalPath = "/music/a/b.flac";

        Assert.Equal("flac", Suffix(song));
    }

    [Fact]
    public void TranscodeDecisionStillReportsMp3ForAnUndownloadedSong()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            ((Microsoft.AspNetCore.Mvc.JsonResult)Builder().CreateTranscodeDecisionResponse(External("gdstudio", "apple~1"), "https")).Value);

        Assert.Contains("\"container\":\"mp3\"", json);
    }
}

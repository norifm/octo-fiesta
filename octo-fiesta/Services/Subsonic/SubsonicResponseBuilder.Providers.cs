using Microsoft.Extensions.Options;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;

namespace octo_fiesta.Services.Subsonic;

public partial class SubsonicResponseBuilder
{
    private readonly bool _showExternalProviderInSuffix;

    public SubsonicResponseBuilder(IOptions<SubsonicSettings>? settings = null)
        => _showExternalProviderInSuffix = settings?.Value.ShowExternalProviderInSuffix ?? false;

    private string GetExternalSuffix(Song song)
    {
        if (!_showExternalProviderInSuffix) return "Remote";
        var origin = Enum.TryParse<MusicService>(song.ExternalProvider, true, out var service)
            ? service.ToString() : song.ExternalProvider ?? "Remote";
        if (song.ExternalProvider == "gdstudio" && song.ExternalId is { } id && id.IndexOf('~') is > 0 and var tilde)
            origin += "-" + char.ToUpperInvariant(id[0]) + id[1..tilde];
        return origin;
    }
}

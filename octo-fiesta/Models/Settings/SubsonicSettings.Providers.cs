namespace octo_fiesta.Models.Settings;

public partial class SubsonicSettings
{
    /// <summary>Optional provider list. MUSIC_SERVICE lists are normalized into this setting before binding.</summary>
    public string? MusicServices { get; set; }

    /// <summary>Replace an existing file only when the incoming audio is better. Default: false.</summary>
    public bool AllowBitrateUpgrade { get; set; }

    /// <summary>Show the external origin in the suffix field. Off by default for client compatibility.</summary>
    public bool ShowExternalProviderInSuffix { get; set; }

    public static List<MusicService> ParseMusicServices(string? value, out List<string> unknown)
    {
        unknown = [];
        var result = new List<MusicService>();
        foreach (var part in (value ?? "").Split(['|', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Enum.TryParse also accepts numbers; configuration must use provider names.
            if (Enum.GetNames<MusicService>().Any(n => n.Equals(part, StringComparison.OrdinalIgnoreCase))
                && Enum.TryParse<MusicService>(part, true, out var service))
            {
                if (!result.Contains(service)) result.Add(service);
            }
            else unknown.Add(part);
        }
        if (result.Count == 0 && unknown.Count == 0) result.Add(MusicService.Deezer);
        return result;
    }
}

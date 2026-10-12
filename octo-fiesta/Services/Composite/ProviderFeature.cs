using Microsoft.Extensions.DependencyInjection.Extensions;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.AppleMusic;
using octo_fiesta.Services.Deezer;
using octo_fiesta.Services.GDStudio;
using octo_fiesta.Services.Qobuz;
using octo_fiesta.Services.SquidWTF;
using octo_fiesta.Services.Subsonic;
using octo_fiesta.Services.Tidal;
using octo_fiesta.Services.Validation;
using octo_fiesta.Services.Yandex;

namespace octo_fiesta.Services.Composite;

/// <summary>Optional provider composition, kept outside the legacy startup path.</summary>
public static class ProviderFeature
{
    public static void Configure(IServiceCollection services, IConfiguration configuration)
    {
        NormalizeConfiguration(configuration);
        services.Configure<GDStudioSettings>(configuration.GetSection("GDStudio"));
        services.AddHttpClient(GDStudioHttpClientConfiguration.ClientName)
            .ConfigurePrimaryHttpMessageHandler(GDStudioHttpClientConfiguration.CreateHandler);
    }

    public static void NormalizeConfiguration(IConfiguration configuration)
    {
        var raw = configuration["Subsonic:MusicServices"] ?? configuration["Subsonic:MusicService"];
        var parsed = SubsonicSettings.ParseMusicServices(raw, out var unknown);
        if (unknown.Count > 0)
            throw new InvalidOperationException($"Unknown MUSIC_SERVICE entries: {string.Join(", ", unknown)}");
        if (raw is not null)
        {
            configuration["Subsonic:MusicServices"] = string.Join(",", parsed);
            configuration["Subsonic:MusicService"] = parsed[0].ToString();
        }
        foreach (var (key, aliases) in new (string, string[])[]
        {
            ("Source", ["GDSTUDIO_SOURCE"]),
            ("TimeoutSeconds", ["GDSTUDIO_TIMEOUT_SECONDS", "GDSTUDIO__TIMEOUT_SECONDS"]),
            ("Br", ["GDSTUDIO_BR"]), ("Api", ["GDSTUDIO_API"]), ("Proxy", ["GDSTUDIO_PROXY"])
        })
        {
            if (configuration[$"GDStudio:{key}"] is not null) continue;
            var value = aliases.Select(a => configuration[a] ?? Environment.GetEnvironmentVariable(a))
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (value is not null) configuration[$"GDStudio:{key}"] = value;
        }
    }

    public static bool TryRegister(IServiceCollection services, IConfiguration configuration)
    {
        var requested = SubsonicSettings.ParseMusicServices(
            configuration["Subsonic:MusicServices"] ?? configuration["Subsonic:MusicService"], out var unknown);
        if (unknown.Count > 0) throw new InvalidOperationException($"Unknown MUSIC_SERVICE entries: {string.Join(", ", unknown)}");
        if (requested.Count == 1 && requested[0] != MusicService.GDStudio) return false;

        var warnings = new List<string>();
        var active = requested.Where(service =>
        {
            var missing = requested.Count > 1 ? MissingCredentials(configuration, service) : null;
            if (missing is null) return true;
            warnings.Add($"Skipping music service {service}: {missing}");
            return false;
        }).ToList();
        // Preserve the existing alacarte opt-in even when other providers are listed explicitly.
        if (!active.Contains(MusicService.AppleMusic) && AppleMusicRegistration.IsConfigured(configuration))
            active.Add(MusicService.AppleMusic);
        if (active.Count == 0) throw new InvalidOperationException("No usable MUSIC_SERVICE configured. " + string.Join("; ", warnings));

        foreach (var service in active)
        {
            switch (service)
            {
                case MusicService.Qobuz: services.TryAddSingleton<QobuzBundleService>(); break;
                case MusicService.Tidal:
                    services.AddSingleton<TidalTokenStore>();
                    services.AddSingleton<TidalAuthService>();
                    break;
                case MusicService.SquidWTF:
                    services.AddSingleton<SquidWTFInstanceManager>();
                    services.AddSingleton<SquidWTFCaptchaSolver>();
                    break;
                case MusicService.AppleMusic: AppleMusicRegistration.AddClient(services); break;
                case MusicService.GDStudio:
                    services.AddOptions<GDStudioSettings>().Validate(s =>
                        s.Sources.Length > 0 && s.TimeoutSeconds > 0 && s.TimeoutSeconds <= 120
                        && GDStudioSettings.ValidBr.Contains(s.Br)
                        && Uri.TryCreate(s.Api, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http",
                        "GDStudio requires at least one source, a timeout of 1–120 seconds, a supported bitrate, and an HTTP(S) API URL.")
                        .ValidateOnStart();
                    services.AddSingleton<IStartupValidator, GDStudioStartupValidator>();
                    break;
            }
        }
        var providers = active.Select(Descriptor).ToList();
        foreach (var provider in providers)
        {
            services.AddSingleton(provider.Metadata);
            services.AddSingleton(provider.Download);
            // Expose concrete providers for existing playlist consumers without changing those consumers.
            services.AddSingleton(typeof(IMusicMetadataService), sp => sp.GetRequiredService(provider.Metadata));
            services.AddSingleton(typeof(IDownloadService), sp => sp.GetRequiredService(provider.Download));
        }
        services.AddSingleton<IMusicMetadataService>(sp => new CompositeMetadataService(
            providers.Select(p => (p.Key, (IMusicMetadataService)sp.GetRequiredService(p.Metadata))).ToList(),
            sp.GetRequiredService<ILogger<CompositeMetadataService>>()));
        services.AddSingleton<IDownloadService>(sp => new CompositeDownloadService(
            providers.Select(p => (p.Key, (IDownloadService)sp.GetRequiredService(p.Download))).ToList()));
        if (configuration.GetValue("Subsonic:EnableExternalPlaylists", true) && active.Any(s =>
            s is not (MusicService.GDStudio or MusicService.SquidWTF) ||
            (s == MusicService.SquidWTF && string.Equals(configuration["SquidWTF:Source"], "Tidal", StringComparison.OrdinalIgnoreCase))))
            services.AddSingleton<PlaylistSyncService>();
        if (warnings.Count > 0)
            services.AddSingleton<IHostedService>(sp => new ProviderWarnings(warnings, sp.GetRequiredService<ILogger<CompositeMetadataService>>()));
        return true;
    }

    private static string? MissingCredentials(IConfiguration config, MusicService service)
    {
        bool Has(string key) => !string.IsNullOrWhiteSpace(config[key]);
        return service switch
        {
            MusicService.Deezer when !Has("Deezer:Arl") && !Has("Deezer:ArlFallback") => "Deezer__Arl is not set",
            MusicService.Qobuz when !Has("Qobuz:UserId") || !Has("Qobuz:UserAuthToken") => "Qobuz credentials are not set",
            MusicService.Yandex when !Has("Yandex:OAuthToken") => "Yandex__OAuthToken is not set",
            MusicService.Tidal when !Has("Tidal:AccessToken") && !Has("Tidal:RefreshToken")
                && !File.Exists(config["Tidal:TokenStore"] ?? "./tidal-tokens.json") => "no Tidal tokens configured",
            MusicService.AppleMusic when !AppleMusicRegistration.IsConfigured(config) => "alacarte credentials are not set",
            _ => null
        };
    }

    private static (string Key, Type Metadata, Type Download) Descriptor(MusicService service) => service switch
    {
        MusicService.Deezer => ("deezer", typeof(DeezerMetadataService), typeof(DeezerDownloadService)),
        MusicService.Qobuz => ("qobuz", typeof(QobuzMetadataService), typeof(QobuzDownloadService)),
        MusicService.Tidal => ("tidal", typeof(TidalMetadataService), typeof(TidalDownloadService)),
        MusicService.Yandex => ("yandex", typeof(YandexMetadataService), typeof(YandexDownloadService)),
        MusicService.SquidWTF => ("squidwtf", typeof(SquidWTFMetadataService), typeof(SquidWTFDownloadService)),
        MusicService.AppleMusic => ("applemusic", typeof(AppleMusicMetadataService), typeof(AppleMusicDownloadService)),
        MusicService.GDStudio => ("gdstudio", typeof(GDStudioMetadataService), typeof(GDStudioDownloadService)),
        _ => throw new ArgumentOutOfRangeException(nameof(service))
    };

    private sealed class ProviderWarnings(List<string> warnings, ILogger logger) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            foreach (var warning in warnings) logger.LogWarning("{Warning}", warning);
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

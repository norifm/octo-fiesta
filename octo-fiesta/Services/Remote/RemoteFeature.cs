namespace octo_fiesta.Services.Remote;

/// <summary>Registers nori's remote control and jam relay: the hub, and the guest key check ahead of the pipeline.</summary>
public static class RemoteFeature
{
    public static IServiceCollection AddNoriRemote(this IServiceCollection services)
    {
        services.AddSingleton<RemoteHub>();
        services.AddTransient<IStartupFilter, GuestFirst>();
        return services;
    }

    /// <summary>Puts <see cref="JamGuestMiddleware"/> before every other middleware, the Subsonic authentication included.</summary>
    private sealed class GuestFirst : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseMiddleware<JamGuestMiddleware>();
            next(app);
        };
    }
}

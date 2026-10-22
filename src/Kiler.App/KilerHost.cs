using System.Net;

namespace Kiler.App;

/// <summary>The local web server: Kestrel on 127.0.0.1 only, with the Kiler API behind the access check.</summary>
internal static class KilerHost
{
    /// <summary>
    /// How long the stop waits for open requests before it aborts them. Shorter than <see cref="SingleInstance.Wait"/>,
    /// so a second start that finds this Kiler stopping gets the lock in time. Without the limit (30 s), a client without
    /// the token could hold every stop up with a request that it never finishes, because the guard sees only whole requests.
    /// </summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    public static WebApplication Build(StartOptions options)
    {
        // The empty builder reads no config files and no environment variables, so nothing
        // outside the command line can change the address, and nothing watches the exe folder.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = ShutdownTimeout);
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.Port ?? 0));
        builder.Services.AddRoutingCore();
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.TypeInfoResolverChain.Insert(0, KilerJsonContext.Default));

        var token = new AccessToken();
        builder.Services.AddSingleton(token);
        var times = options.HeartbeatTimes;
        builder.Services.AddSingleton(services => new Heartbeat(services.GetRequiredService<IHostApplicationLifetime>(), times, TimeProvider.System));

        var app = builder.Build();
        // Made now, not on the first request, so the wait for the first page starts with the server.
        var heartbeat = app.Services.GetRequiredService<Heartbeat>();
        // Routing comes after the guard. WebApplication would otherwise put it first, and an endpoint with
        // ShortCircuit() would run straight from routing, before the guard.
        AccessGuard.Use(app, token);
        app.UseRouting();
        HealthApi.Map(app, heartbeat);
        EventsApi.Map(app, heartbeat, times.PingInterval, TimeProvider.System, app.Lifetime.ApplicationStopping);
        return app;
    }
}

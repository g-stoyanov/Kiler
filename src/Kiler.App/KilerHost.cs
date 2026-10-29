using System.Net;
using Kiler.Core;
using Microsoft.Extensions.FileProviders;

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

    /// <param name="options">The parameters of the start.</param>
    /// <param name="uiFiles">The UI files; by default the ones embedded in Kiler.exe (<see cref="UiFiles.Embedded"/>).</param>
    /// <param name="data">
    /// The data of the start (M2.10), which the caller disposes after the server has stopped; null runs without a data folder
    /// (<see cref="KilerData.WithoutFolder"/>). Tests give one with folders of their own, so no test reads <c>share.json</c>
    /// or makes a local copy in the profile.
    /// </param>
    /// <param name="account">
    /// The account of this Kiler (M4.1); by default the one of <c>--account</c>, else of the process
    /// (<see cref="WindowsAccount"/>). The API tests give a fake.
    /// </param>
    public static WebApplication Build(StartOptions options, IFileProvider? uiFiles = null, KilerData? data = null, ICurrentAccount? account = null)
    {
        // Always in DI, so every endpoint finds the state of the data (M4.2).
        data ??= KilerData.WithoutFolder();
        account ??= new WindowsAccount(options.Account);
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
        builder.Services.AddSingleton(data);
        builder.Services.AddSingleton(account);
        var sessions = new CurrentSession();
        builder.Services.AddSingleton(sessions);

        var app = builder.Build();
        // Once the server listens, so a share that cannot be reached does not hold up the page. The watch stops before the
        // server does; the stops registered later, such as that of the instance file, run before it.
        app.Lifetime.ApplicationStarted.Register(data.Start);
        app.Lifetime.ApplicationStopping.Register(() => data.Stop(ShutdownTimeout));
        // Once the server has stopped, so no request starts with the keys; one that outlived the stop answers as locked.
        app.Lifetime.ApplicationStopped.Register(sessions.Dispose);

        // Made now, not on the first request, so the wait for the first page starts with the server.
        var heartbeat = app.Services.GetRequiredService<Heartbeat>();
        // Routing comes after the guard. WebApplication would otherwise put it first, and an endpoint with
        // ShortCircuit() would run straight from routing, before the guard.
        AccessGuard.Use(app, token, InstancePlace.CookieNameFor(options.DataFolder));
        app.UseRouting();
        // After routing, so it knows the endpoint and what it needs (M4.4).
        AccessRules.Use(app, sessions, data);
        UiFiles.Use(app, uiFiles ?? UiFiles.Embedded());
        HealthApi.Map(app, heartbeat);
        DataApi.Map(app, data);
        WizardApi.Map(app, data, account);
        SessionApi.Map(app, sessions, data, account);
        EventsApi.Map(app, heartbeat, times.PingInterval, TimeProvider.System, app.Lifetime.ApplicationStopping);
        return app;
    }
}

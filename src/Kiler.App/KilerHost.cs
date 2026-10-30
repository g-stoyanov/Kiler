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
    /// <param name="time">
    /// The clock of the heartbeat, the event stream and the automatic lock; by default the system's. The API tests of the
    /// automatic lock give a fake one, and keep a page open, as the heartbeat runs on it too.
    /// </param>
    public static WebApplication Build(
        StartOptions options, IFileProvider? uiFiles = null, KilerData? data = null, ICurrentAccount? account = null, TimeProvider? time = null)
    {
        // Always in DI, so every endpoint finds the state of the data (M4.2).
        data ??= KilerData.WithoutFolder();
        account ??= new WindowsAccount(options.Account);
        time ??= TimeProvider.System;
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
        builder.Services.AddSingleton(services => new Heartbeat(services.GetRequiredService<IHostApplicationLifetime>(), times, time));
        builder.Services.AddSingleton(data);
        builder.Services.AddSingleton(account);
        var sessions = new CurrentSession();
        builder.Services.AddSingleton(sessions);
        var autoLock = new AutoLock(sessions, () => data.Files, time);

        var app = builder.Build();
        // Once the server listens, so a share that cannot be reached does not hold up the page. The watch stops before the
        // server does; the stops registered later, such as that of the instance file, run before it.
        app.Lifetime.ApplicationStarted.Register(data.Start);
        app.Lifetime.ApplicationStopping.Register(() => data.Stop(ShutdownTimeout));
        // Once the server has stopped, so no request starts with the keys; one that outlived the stop answers as locked.
        app.Lifetime.ApplicationStopped.Register(sessions.Dispose);
        app.Lifetime.ApplicationStopped.Register(autoLock.Dispose);

        // Made now, not on the first request, so the wait for the first page starts with the server.
        var heartbeat = app.Services.GetRequiredService<Heartbeat>();
        // Routing comes after the guard. WebApplication would otherwise put it first, and an endpoint with
        // ShortCircuit() would run straight from routing, before the guard.
        AccessGuard.Use(app, token, InstancePlace.CookieNameFor(options.DataFolder));
        app.UseRouting();
        // After routing, so it knows the endpoint, what it needs (M4.4) and whether it is an activity (M4.5b).
        AccessRules.Use(app, sessions, data, autoLock);
        UiFiles.Use(app, uiFiles ?? UiFiles.Embedded());
        HealthApi.Map(app, heartbeat);
        DataApi.Map(app, data);
        WizardApi.Map(app, data, account);
        SessionApi.Map(app, sessions, data, account);
        PasswordApi.Map(app, sessions, data, account);
        AdminApi.Map(app, data);
        EventsApi.Map(app, heartbeat, sessions, times.PingInterval, time, app.Lifetime.ApplicationStopping);
        return app;
    }
}

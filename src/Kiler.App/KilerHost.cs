using System.Net;

namespace Kiler.App;

/// <summary>The local web server: Kestrel on 127.0.0.1 only, with the Kiler API behind the access check.</summary>
internal static class KilerHost
{
    public static WebApplication Build(StartOptions options)
    {
        // The empty builder reads no config files and no environment variables, so nothing
        // outside the command line can change the address, and nothing watches the exe folder.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.Port ?? 0));
        builder.Services.AddRoutingCore();
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.TypeInfoResolverChain.Insert(0, KilerJsonContext.Default));

        var token = new AccessToken();
        builder.Services.AddSingleton(token);

        var app = builder.Build();
        // Routing comes after the guard. WebApplication would otherwise put it first, and an endpoint with
        // ShortCircuit() would run straight from routing, before the guard.
        AccessGuard.Use(app, token);
        app.UseRouting();
        HealthApi.Map(app);
        return app;
    }
}

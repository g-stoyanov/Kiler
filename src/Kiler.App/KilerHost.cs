using System.Net;

namespace Kiler.App;

/// <summary>The local web server: Kestrel on 127.0.0.1 only, with the Kiler API.</summary>
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

        var app = builder.Build();
        HealthApi.Map(app);
        return app;
    }
}

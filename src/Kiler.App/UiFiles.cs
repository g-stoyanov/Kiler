using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace Kiler.App;

/// <summary>
/// The UI: the Vite build of <c>src/ui</c>, which a Release build embeds in Kiler.exe (Kiler.App.csproj). It is served
/// after the guard and after routing, so an endpoint wins over a file. Outside <c>/api</c>:
/// <list type="bullet">
/// <item>a file that exists is served with the type of its extension;</item>
/// <item>a GET of a page address, one without a dot in its last segment, gets <c>index.html</c>, so a reload on an address
/// of the UI's own routing works;</item>
/// <item>anything else is 404.</item>
/// </list>
/// <c>/api</c> and everything under it never gets a file, so an address there without an endpoint is 404.
/// </summary>
internal static class UiFiles
{
    /// <summary>
    /// The policy of every UI file. Only the page itself and the workers apply it: a worker takes the policy of its own
    /// script, not the page's. <c>data:</c> images are the small ones that Vite puts inline. <c>frame-ancestors</c>: no
    /// other page may frame Kiler; the guard refuses a frame from another site too (<c>Sec-Fetch-Site</c>).
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>Vite puts the files whose names carry a hash of their content here (<c>build.assetsDir</c>).</summary>
    public const string HashedFolder = "/assets";

    /// <summary>Cached for a year without a check: a file with other content has another name.</summary>
    public const string HashedCacheControl = "private, max-age=31536000, immutable";

    /// <summary>
    /// The page is never cached. A page that loads after Kiler has stopped joins the events worker and opens a new stream,
    /// on the grounds that its load has just reached Kiler (<c>src/ui/src/events.ts</c>); a page from the cache would not have.
    /// </summary>
    public const string PageCacheControl = "no-store";

    /// <summary>The other files, such as the icon, are checked with the server each time.</summary>
    public const string OtherCacheControl = "no-cache";

    private const string PagePath = "/index.html";

    /// <summary>The folder of the UI files among the embedded resources, as Kiler.App.csproj names it.</summary>
    private const string EmbeddedRoot = "ui";

    /// <summary>The resource with the list of the embedded files, which the build makes only when it embeds the UI.</summary>
    private const string EmbeddedManifest = "Microsoft.Extensions.FileProviders.Embedded.Manifest.xml";

    private static readonly PathString Api = new("/api");

    /// <summary>
    /// The files embedded in Kiler.exe. A Debug build embeds none, so there is nothing to serve: in dev mode Vite serves
    /// the UI (<c>npm run dev</c>).
    /// </summary>
    public static IFileProvider Embedded()
    {
        var assembly = typeof(UiFiles).Assembly;
        return assembly.GetManifestResourceInfo(EmbeddedManifest) is null
            ? new NullFileProvider()
            : new ManifestEmbeddedFileProvider(assembly, EmbeddedRoot);
    }

    public static void Use(IApplicationBuilder app, IFileProvider files) =>
        app.UseWhen(
            context => !context.Request.Path.StartsWithSegments(Api),
            ui =>
            {
                ui.Use((context, next) =>
                {
                    if (IsPageAddress(context))
                    {
                        context.Request.Path = PagePath;
                    }

                    return next(context);
                });
                ui.UseStaticFiles(new StaticFileOptions { FileProvider = files, OnPrepareResponse = SetHeaders });
            });

    /// <summary>
    /// An address that no endpoint took and whose last segment has no dot, which a file name would have. The static files
    /// serve only GET and HEAD, so another method gets 404 there.
    /// </summary>
    private static bool IsPageAddress(HttpContext context)
    {
        if (context.GetEndpoint() is not null)
        {
            return false;
        }

        var path = context.Request.Path.Value ?? "/";
        return !path[(path.LastIndexOf('/') + 1)..].Contains('.', StringComparison.Ordinal);
    }

    private static void SetHeaders(StaticFileResponseContext file)
    {
        var path = file.Context.Request.Path;
        var headers = file.Context.Response.Headers;
        headers.CacheControl = path.StartsWithSegments(HashedFolder) ? HashedCacheControl
            : path == PagePath ? PageCacheControl
            : OtherCacheControl;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XFrameOptions = "DENY";
        headers.XContentTypeOptions = "nosniff";
        // The page links to other sites (documentation, M6); they do not need to know that it is a Kiler on 127.0.0.1.
        // Not no-referrer: with it the browser sends "Origin: null" on Kiler's own POST requests.
        headers["Referrer-Policy"] = "same-origin";
    }
}

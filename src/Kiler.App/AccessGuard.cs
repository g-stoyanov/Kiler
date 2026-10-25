using Microsoft.Extensions.Primitives;

namespace Kiler.App;

/// <summary>
/// The check in front of every request, the UI files included:
/// <list type="number">
/// <item>the <c>Host</c> header names 127.0.0.1 or localhost with the port of the connection, against DNS rebinding (400);</item>
/// <item>the browser does not mark the request as coming from another site (403). Another port of 127.0.0.1 is the
/// same site for the browser, so <c>SameSite=Strict</c> alone lets its requests through with the cookie;</item>
/// <item><c>GET /?t=&lt;token&gt;</c> sets the cookie and redirects to <c>/</c>, which clears the token from the address;</item>
/// <item>any other request needs the cookie (401). Its name is per instance (<see cref="InstancePlace.CookieNameFor"/>).</item>
/// </list>
/// </summary>
internal static class AccessGuard
{
    private const string TokenParameter = "t";
    private const string FetchSiteHeader = "Sec-Fetch-Site";

    public static void Use(IApplicationBuilder app, AccessToken token, string cookieName) =>
        app.Use((context, next) => CheckAsync(context, next, token, cookieName));

    /// <summary>The address that lets a browser in: the root of the server with the token in <c>t</c>.</summary>
    public static Uri EntryAddress(Uri server, AccessToken token) => new(server, $"/?{TokenParameter}={token.Value}");

    /// <summary>
    /// True for <c>127.0.0.1:port</c> and <c>localhost:port</c>, the name in any case. The port is the one the connection
    /// came in on, so it is the bound port, also when the server took a free one. A browser leaves out port 80.
    /// </summary>
    public static bool IsAllowedHost(StringValues host, int port)
    {
        if (host.Count != 1 || host[0] is not { } value)
        {
            return false;
        }

        return IsLoopbackName(value, $":{port}") || (port == 80 && IsLoopbackName(value, ""));

        static bool IsLoopbackName(string value, string portSuffix) =>
            value.Equals($"127.0.0.1{portSuffix}", StringComparison.OrdinalIgnoreCase)
            || value.Equals($"localhost{portSuffix}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the browser says the request comes from a Kiler page (<c>same-origin</c>) or from the user, such as
    /// the address bar (<c>none</c>). A client without the header, such as a test or a script, is judged by the cookie.
    /// </summary>
    private static bool IsAllowedFetchSite(StringValues site) =>
        site.Count == 0 || (site.Count == 1 && site[0] is "same-origin" or "none");

    private static Task CheckAsync(HttpContext context, RequestDelegate next, AccessToken token, string cookieName)
    {
        var request = context.Request;
        var response = context.Response;

        if (!IsAllowedHost(request.Headers.Host, context.Connection.LocalPort))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        }

        if (!IsAllowedFetchSite(request.Headers[FetchSiteHeader]))
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        if (HttpMethods.IsGet(request.Method) && request.Path == "/" && request.Query.TryGetValue(TokenParameter, out var fromAddress))
        {
            if (fromAddress.Count != 1 || !token.Matches(fromAddress[0]))
            {
                response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            // No Expires: the cookie ends with the browser session. No Domain: only this host gets it.
            // Not Secure, because the server is plain HTTP on loopback.
            response.Cookies.Append(cookieName, token.Value, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Path = "/",
            });
            response.Headers.CacheControl = "no-store";
            response.Redirect("/");
            return Task.CompletedTask;
        }

        if (!token.Matches(request.Cookies[cookieName]))
        {
            response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        return next(context);
    }
}

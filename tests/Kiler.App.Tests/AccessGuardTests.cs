using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Primitives;

namespace Kiler.App.Tests;

/// <summary>The protection of the local server, against the real Kestrel.</summary>
public class AccessGuardTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // Every path needs the cookie, the UI files that exist included.
    [Theory]
    [InlineData("/")]
    [InlineData("/api/health")]
    [InlineData("/api/events")]
    [InlineData("/api/unknown")]
    [InlineData("/index.html")]
    [InlineData("/records/12")]
    [InlineData(MadeUpUi.Script)]
    [InlineData(MadeUpUi.Worker)]
    [InlineData(MadeUpUi.Icon)]
    [InlineData("/assets/missing.js")]
    public async Task Request_WithoutCookie_Returns401(string path)
    {
        using var ui = new MadeUpUi();
        await using var server = await KilerServer.StartAsync(ui.Files);
        using var client = server.CreateClient();

        using var response = await client.GetAsync(path, Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("another token")]
    [InlineData("empty")]
    [InlineData("one character less")]
    [InlineData("one character more")]
    [InlineData("last character changed")]
    public async Task Request_WithWrongCookie_Returns401(string wrong)
    {
        await using var server = await KilerServer.StartAsync();
        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("Cookie", $"{server.CookieName}={Spoil(server.Token.Value, wrong)}");

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Request_WithCookie_Returns200()
    {
        await using var server = await KilerServer.StartAsync();
        using var client = server.CreateSignedInClient();

        using var response = await client.GetAsync("/api/health", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Exchange_WithCorrectToken_SetsAnHttpOnlyStrictSessionCookieAndRedirectsToRoot()
    {
        await using var server = await KilerServer.StartAsync();
        using var client = server.CreateClient();

        using var response = await client.GetAsync($"/?t={server.Token.Value}", Cancel);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        Assert.True(response.Headers.CacheControl?.NoStore);

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', StringSplitOptions.TrimEntries);
        Assert.Equal($"{server.CookieName}={server.Token.Value}", cookie[0]);
        // No Expires or Max-Age: the cookie ends with the browser session. No Domain: only this host gets it.
        Assert.Equal(
            ["httponly", "path=/", "samesite=strict"],
            cookie[1..].Select(attribute => attribute.ToLowerInvariant()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Exchange_ForAKilerWithData_SetsTheCookieOfItsInstance()
    {
        const string DataFolder = @"D:\Demo\data";
        await using var server = await KilerServer.StartAsync(new StartOptions { DataFolder = DataFolder });
        using var client = server.CreateClient();

        using var response = await client.GetAsync($"/?t={server.Token.Value}", Cancel);

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', StringSplitOptions.TrimEntries);
        Assert.Equal($"{InstancePlace.For(DataFolder, @"C:\Local\Kiler", 1).CookieName}={server.Token.Value}", cookie[0]);
        Assert.Matches("^kiler-[0-9a-f]{16}=", cookie[0]);
    }

    // Cookies are not separated by port, so the browser sends the cookies of every Kiler to every one of them.
    // Each reads only the cookie of its own instance.
    [Theory]
    [InlineData(null, "kiler-0123456789abcdef")]
    [InlineData(@"D:\Demo\data", "kiler")]
    [InlineData(@"D:\Demo\data", "kiler-0123456789abcdef")]
    public async Task Request_WithTheTokenInTheCookieOfAnotherInstance_Returns401(string? dataFolder, string otherCookie)
    {
        await using var server = await KilerServer.StartAsync(new StartOptions { DataFolder = dataFolder });
        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("Cookie", $"{otherCookie}={server.Token.Value}");

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Request_WithTheCookiesOfTwoInstances_ReadsItsOwn()
    {
        await using var server = await KilerServer.StartAsync(new StartOptions { DataFolder = @"D:\Demo\data" });
        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("Cookie", $"kiler={new AccessToken().Value}; {server.CookieName}={server.Token.Value}");

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("another token")]
    [InlineData("empty")]
    [InlineData("last character changed")]
    [InlineData("given twice")]
    public async Task Exchange_WithWrongToken_Returns401WithoutACookie(string wrong)
    {
        await using var server = await KilerServer.StartAsync();
        using var client = server.CreateClient();
        var query = wrong == "given twice"
            ? $"t={server.Token.Value}&t={server.Token.Value}"
            : $"t={Spoil(server.Token.Value, wrong)}";

        using var response = await client.GetAsync($"/?{query}", Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    // Only GET / exchanges the token.
    [Theory]
    [InlineData("GET", "/api/health?t={token}")]
    [InlineData("GET", "/index.html?t={token}")]
    [InlineData("POST", "/?t={token}")]
    [InlineData("HEAD", "/?t={token}")]
    public async Task TokenOutsideGetRoot_Returns401WithoutACookie(string method, string path)
    {
        await using var server = await KilerServer.StartAsync();
        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path.Replace("{token}", server.Token.Value, StringComparison.Ordinal));

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    // A short-circuit endpoint runs right after routing and skips the middleware after it.
    [Fact]
    public async Task ShortCircuitEndpoint_StillNeedsTheCookie()
    {
        var app = KilerHost.Build(new StartOptions());
        app.MapGet("/short", () => "reached").ShortCircuit();
        await using var server = await KilerServer.StartAsync(app);
        using var client = server.CreateClient();

        using var response = await client.GetAsync("/short", Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Browser_OpeningTheEntryAddress_EndsOnRootWithoutTheTokenAndIsSignedIn()
    {
        await using var server = await KilerServer.StartAsync();
        using var browser = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });

        using var entry = await browser.GetAsync(AccessGuard.EntryAddress(server.Address, server.Token), Cancel);
        using var health = await browser.GetAsync(new Uri(server.Address, "/api/health"), Cancel);

        Assert.Equal(new Uri(server.Address, "/"), entry.RequestMessage?.RequestUri);
        Assert.NotEqual(HttpStatusCode.Unauthorized, entry.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Request_WithForeignHost_Returns400()
    {
        await using var server = await KilerServer.StartAsync();
        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Host = $"evil.example:{server.Port}";

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // DNS rebinding: a foreign name that resolves to 127.0.0.1. The browser would not send the cookie
    // there, but the cookie must not let such a request through either.
    [Theory]
    [InlineData("evil.example:{port}")]
    [InlineData("evil.example")]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1:{other}")]
    [InlineData("localhost:{other}")]
    [InlineData("127.0.0.2:{port}")]
    [InlineData("[::1]:{port}")]
    [InlineData("localhost.:{port}")]
    public async Task Request_WithForeignHost_Returns400EvenWithTheCookie(string host)
    {
        await using var server = await KilerServer.StartAsync();
        using var client = server.CreateSignedInClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Host = WithPort(host, server.Port);

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Exchange_WithForeignHost_Returns400WithoutACookie()
    {
        await using var server = await KilerServer.StartAsync();
        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/?t={server.Token.Value}");
        request.Headers.Host = $"evil.example:{server.Port}";

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("127.0.0.1:{port}")]
    [InlineData("localhost:{port}")]
    [InlineData("LocalHost:{port}")]
    public async Task Request_WithLoopbackHostAndPort_IsAccepted(string host)
    {
        await using var server = await KilerServer.StartAsync();
        using var client = server.CreateSignedInClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Host = WithPort(host, server.Port);

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Another port of 127.0.0.1 is the same site for the browser, so SameSite=Strict does not stop
    // its requests, and they carry the cookie. Sec-Fetch-Site tells them apart.
    [Theory]
    [InlineData("same-site")]
    [InlineData("cross-site")]
    [InlineData("unknown")]
    [InlineData("")]
    public async Task Request_FromAnotherSite_Returns403EvenWithTheCookie(string site)
    {
        await using var server = await KilerServer.StartAsync();
        using var client = server.CreateSignedInClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", site);

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("same-origin")]
    [InlineData("none")]
    public async Task Request_FromTheKilerPageOrTheUser_IsAccepted(string site)
    {
        await using var server = await KilerServer.StartAsync();
        using var client = server.CreateSignedInClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("Sec-Fetch-Site", site);

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Post_FromAPageOnAnotherPortOfThisMachine_Returns403()
    {
        await using var server = await KilerServer.StartAsync();
        using var client = server.CreateSignedInClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/health");
        request.Headers.Add("Sec-Fetch-Site", "same-site");

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Exchange_FromAnotherSite_Returns403WithoutACookie()
    {
        await using var server = await KilerServer.StartAsync();
        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/?t={server.Token.Value}");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("127.0.0.1:5050", 5050, true)]
    [InlineData("localhost:5050", 5050, true)]
    [InlineData("LOCALHOST:5050", 5050, true)]
    // A browser leaves out the default port 80.
    [InlineData("127.0.0.1", 80, true)]
    [InlineData("localhost", 80, true)]
    [InlineData("127.0.0.1:80", 80, true)]
    [InlineData("127.0.0.1", 5050, false)]
    [InlineData("localhost", 5050, false)]
    [InlineData("127.0.0.1:5051", 5050, false)]
    [InlineData("127.0.0.1:05050", 5050, false)]
    [InlineData("127.0.0.1:5050.evil.example", 5050, false)]
    [InlineData(" 127.0.0.1:5050", 5050, false)]
    [InlineData("evil.example:5050", 5050, false)]
    [InlineData("[::1]:5050", 5050, false)]
    [InlineData("", 5050, false)]
    [InlineData("", 80, false)]
    public void IsAllowedHost_AcceptsOnlyLoopbackNamesWithThePort(string host, int port, bool allowed)
    {
        Assert.Equal(allowed, AccessGuard.IsAllowedHost(host, port));
    }

    [Fact]
    public void IsAllowedHost_WithNoHostOrTwoHosts_IsFalse()
    {
        Assert.False(AccessGuard.IsAllowedHost(StringValues.Empty, 5050));
        Assert.False(AccessGuard.IsAllowedHost(new StringValues(["127.0.0.1:5050", "127.0.0.1:5050"]), 5050));
    }

    [Fact]
    public void EntryAddress_IsTheRootWithTheTokenInT()
    {
        var token = new AccessToken();

        var address = AccessGuard.EntryAddress(new Uri("http://127.0.0.1:5050"), token);

        Assert.Equal($"http://127.0.0.1:5050/?t={token.Value}", address.AbsoluteUri);
    }

    private static string Spoil(string token, string how) => how switch
    {
        "another token" => new AccessToken().Value,
        "empty" => "",
        "one character less" => token[..^1],
        "one character more" => token + "A",
        "last character changed" => token[..^1] + (token[^1] == 'A' ? 'B' : 'A'),
        _ => throw new ArgumentOutOfRangeException(nameof(how), how, null),
    };

    /// <summary>Puts the server port in place of <c>{port}</c> and another port in place of <c>{other}</c>.</summary>
    private static string WithPort(string host, int port) =>
        host.Replace("{port}", $"{port}", StringComparison.Ordinal)
            .Replace("{other}", $"{(port == 65535 ? port - 1 : port + 1)}", StringComparison.Ordinal);
}

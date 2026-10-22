using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Kiler.App.Tests;

/// <summary>
/// Kiler on the real Kestrel, on a free port of 127.0.0.1, for API tests. The test server of
/// WebApplicationFactory has no real port, so the Host check rejects every request to it.
/// </summary>
internal sealed class KilerServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private KilerServer(WebApplication app)
    {
        _app = app;
        Address = new Uri(Assert.Single(app.Urls));
        Token = app.Services.GetRequiredService<AccessToken>();
    }

    /// <summary>The address of the server, <c>http://127.0.0.1:port</c>.</summary>
    public Uri Address { get; }

    public int Port => Address.Port;

    public AccessToken Token { get; }

    public static Task<KilerServer> StartAsync() => StartAsync(KilerHost.Build(new StartOptions()));

    /// <summary>Starts an app from <see cref="KilerHost.Build"/> that the test has changed, for example with an extra endpoint.</summary>
    public static async Task<KilerServer> StartAsync(WebApplication app)
    {
        try
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        return new KilerServer(app);
    }

    /// <summary>A client that keeps no cookies and does not follow redirects, so each test sets what it sends.</summary>
    public HttpClient CreateClient() =>
        new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = Address };

    /// <summary>The same client with the cookie that the address with the token gives.</summary>
    public HttpClient CreateSignedInClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{AccessGuard.CookieName}={Token.Value}");
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
    }
}

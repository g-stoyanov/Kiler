using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Kiler.App.Tests;

/// <summary>
/// Kiler on the real Kestrel, on a free port of 127.0.0.1, for API tests. The test server of
/// WebApplicationFactory has no real port, so the Host check rejects every request to it.
/// </summary>
internal sealed class KilerServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    /// <summary>The wait for the stop, as <c>Program</c> runs it, so a stop by the heartbeat stops the server.</summary>
    private readonly Task _shutdown;

    private KilerServer(WebApplication app, string? dataFolder)
    {
        _app = app;
        Address = new Uri(Assert.Single(app.Urls));
        Token = app.Services.GetRequiredService<AccessToken>();
        CookieName = InstancePlace.CookieNameFor(dataFolder);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());
        Stopping = stopping.Task;
        _shutdown = app.WaitForShutdownAsync(CancellationToken.None);
    }

    /// <summary>The address of the server, <c>http://127.0.0.1:port</c>.</summary>
    public Uri Address { get; }

    public int Port => Address.Port;

    public AccessToken Token { get; }

    /// <summary>The services of the app, such as the session of the process (M4.4).</summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>The name of the cookie with the token, which comes from the data folder of the instance.</summary>
    public string CookieName { get; }

    /// <summary>Completes when the host starts to stop: by the heartbeat, or by <see cref="StopAsync"/>.</summary>
    public Task Stopping { get; }

    /// <summary>Completes when the server has stopped and let the port go.</summary>
    public Task Stopped => _shutdown;

    public static Task<KilerServer> StartAsync() => StartAsync(new StartOptions());

    public static Task<KilerServer> StartAsync(StartOptions options) => StartAsync(KilerHost.Build(options), options.DataFolder);

    /// <summary>Starts Kiler with UI files of the test instead of the embedded ones.</summary>
    public static Task<KilerServer> StartAsync(IFileProvider uiFiles) => StartAsync(KilerHost.Build(new StartOptions(), uiFiles));

    /// <summary>
    /// Starts an app from <see cref="KilerHost.Build"/> that the test has changed, for example with an extra endpoint.
    /// <paramref name="dataFolder"/> is the one the app was built with.
    /// </summary>
    public static async Task<KilerServer> StartAsync(WebApplication app, string? dataFolder = null)
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

        return new KilerServer(app, dataFolder);
    }

    /// <summary>A client that keeps no cookies and does not follow redirects, so each test sets what it sends.</summary>
    public HttpClient CreateClient() =>
        new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = Address };

    /// <summary>The same client with the cookie that the address with the token gives.</summary>
    public HttpClient CreateSignedInClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{CookieName}={Token.Value}");
        return client;
    }

    /// <summary>Stops the server as Ctrl+C does.</summary>
    public async Task StopAsync()
    {
        _app.Lifetime.StopApplication();
        await _shutdown;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _app.DisposeAsync();
    }
}

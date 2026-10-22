using System.Net;

namespace Kiler.App;

/// <summary>Asks a running Kiler whether it answers with its token.</summary>
internal interface IHealthCheck
{
    Task<bool> AnswersAsync(RunningInstance instance, CancellationToken cancellationToken);
}

/// <summary><c>GET /api/health</c> with the cookie of the token, as a browser that has signed in sends it.</summary>
internal sealed class HealthCheck : IHealthCheck, IDisposable
{
    /// <summary>One try is short. A Kiler that does not answer in this time gets another try (<see cref="SingleInstance"/>).</summary>
    private static readonly TimeSpan TryTimeout = TimeSpan.FromSeconds(2);

    // No proxy: the request carries the token, and it goes only to 127.0.0.1. No cookies are kept and no redirect is followed.
    private readonly HttpClient _client = new(new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false })
    {
        Timeout = TryTimeout,
    };

    public async Task<bool> AnswersAsync(RunningInstance instance, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(instance.Server, "/api/health"));
        request.Headers.Add("Cookie", $"{AccessGuard.CookieName}={instance.Token.Value}");
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        // HttpClient reports its own timeout as a cancellation.
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public void Dispose() => _client.Dispose();
}

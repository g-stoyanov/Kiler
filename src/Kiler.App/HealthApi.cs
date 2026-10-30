using System.Reflection;
using Kiler.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kiler.App;

internal sealed record HealthResponse(string Version);

/// <summary>
/// <c>GET /api/health</c>: the server is up, and which version it runs. A second start asks it before it opens the
/// browser (<see cref="HealthCheck"/>), so the answer puts the stop off until that browser has had time to open the page
/// (<see cref="Heartbeat"/>). Once the server is stopping, the answer is 503, and the second start waits for the lock and
/// starts a new server instead of sending the browser to one that is going away.
/// </summary>
internal static class HealthApi
{
    /// <summary>The product version, without the build metadata (the commit after "+").</summary>
    public static string Version { get; } = ReadVersion();

    public static void Map(IEndpointRouteBuilder endpoints, Heartbeat heartbeat) =>
        endpoints.MapGet("/api/health", Results<Ok<HealthResponse>, StatusCodeHttpResult> () =>
            heartbeat.TryPostpone()
                ? TypedResults.Ok(new HealthResponse(Version))
                : TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable)).Needs(Need.Nothing).NotActivity();

    private static string ReadVersion()
    {
        var version = typeof(HealthApi).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? throw new InvalidOperationException("The assembly has no informational version.");
        var metadata = version.IndexOf('+', StringComparison.Ordinal);
        return metadata < 0 ? version : version[..metadata];
    }
}

using System.Reflection;

namespace Kiler.App;

internal sealed record HealthResponse(string Version);

/// <summary><c>GET /api/health</c>: the server is up, and which version it runs.</summary>
internal static class HealthApi
{
    /// <summary>The product version, without the build metadata (the commit after "+").</summary>
    public static string Version { get; } = ReadVersion();

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/health", () => TypedResults.Ok(new HealthResponse(Version)));

    private static string ReadVersion()
    {
        var version = typeof(HealthApi).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? throw new InvalidOperationException("The assembly has no informational version.");
        var metadata = version.IndexOf('+', StringComparison.Ordinal);
        return metadata < 0 ? version : version[..metadata];
    }
}

using System.Net;

namespace Kiler.App.Tests;

public class HealthApiTests
{
    [Fact]
    public async Task GetHealth_Returns200WithTheVersionAsJson()
    {
        await using var server = await KilerServer.StartAsync();
        using var client = server.CreateSignedInClient();

        using var response = await client.GetAsync("/api/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal($$"""{"version":"{{HealthApi.Version}}"}""", body);
    }

    [Fact]
    public void Version_IsTheProductVersionWithoutBuildMetadata()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", HealthApi.Version);
    }
}

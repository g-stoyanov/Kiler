using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kiler.App.Tests;

public class HealthApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task GetHealth_Returns200WithTheVersionAsJson()
    {
        using var client = factory.CreateClient();

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

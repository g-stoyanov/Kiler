using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;

namespace Kiler.App.Tests;

/// <summary>Tests that change the process environment run alone, after all the other tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}

/// <summary>The real Kestrel server, not the test server of WebApplicationFactory.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public class KilerHostTests
{
    [Fact]
    public async Task Start_WithoutPort_ListensOnlyOn127001OnAFreePort()
    {
        await using var app = KilerHost.Build(new StartOptions());
        await app.StartAsync(TestContext.Current.CancellationToken);

        var port = AssertListensOnlyOnLoopback(app);
        Assert.NotEqual(0, port);

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_WithAddressSettingsInTheEnvironment_StillListensOnlyOn127001()
    {
        // With the full or the slim builder, these would move the server to every interface.
        var settings = new Dictionary<string, string>
        {
            ["ASPNETCORE_URLS"] = "http://0.0.0.0:0",
            ["DOTNET_URLS"] = "http://0.0.0.0:0",
            ["URLS"] = "http://0.0.0.0:0",
            ["ASPNETCORE_HTTP_PORTS"] = "0",
            ["ASPNETCORE_PREFERHOSTINGURLS"] = "true",
            ["DOTNET_PREFERHOSTINGURLS"] = "true",
        };
        var before = settings.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (name, value) in settings)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            await using var app = KilerHost.Build(new StartOptions());
            await app.StartAsync(TestContext.Current.CancellationToken);

            AssertListensOnlyOnLoopback(app);

            await app.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            foreach (var (name, value) in before)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    [Fact]
    public async Task Start_WithPort_ListensOnThatPortAndAnswersHealth()
    {
        var port = FreePort();
        await using var app = KilerHost.Build(new StartOptions { Port = port });
        await app.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(port, AssertListensOnlyOnLoopback(app));
        using var client = new HttpClient();
        using var response = await client.GetAsync($"http://127.0.0.1:{port}/api/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_OnAPortInUse_ThrowsIOException()
    {
        // Program reports a busy --port by catching IOException.
        var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        try
        {
            var port = ((IPEndPoint)busy.LocalEndpoint).Port;
            await using var app = KilerHost.Build(new StartOptions { Port = port });

            await Assert.ThrowsAnyAsync<IOException>(() => app.StartAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            busy.Stop();
        }
    }

    /// <summary>Checks the one address of the server and what the operating system has open on its port; returns the port.</summary>
    private static int AssertListensOnlyOnLoopback(WebApplication app)
    {
        var address = new Uri(Assert.Single(app.Urls));
        Assert.Equal("127.0.0.1", address.Host);

        // Every address, IPv4 and IPv6.
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Where(endpoint => endpoint.Port == address.Port)
            .ToList();
        Assert.NotEmpty(listeners);
        Assert.All(listeners, endpoint => Assert.True(IPAddress.IsLoopback(endpoint.Address), $"{endpoint} is not a loopback address."));
        return address.Port;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

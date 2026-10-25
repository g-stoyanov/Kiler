using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Kiler.App.Tests;

/// <summary>The health check against the real Kiler server and against ports where no Kiler answers.</summary>
public class HealthCheckTests
{
    /// <summary>The cookie name where no Kiler answers, so the name does not matter.</summary>
    private const string AnyCookie = "kiler";

    [Fact]
    public async Task Answers_WithTheTokenOfTheServer_IsTrue()
    {
        await using var server = await KilerServer.StartAsync();
        using var health = new HealthCheck(server.CookieName);

        Assert.True(await health.AnswersAsync(new RunningInstance(server.Port, server.Token), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Answers_AKilerWithData_WithTheCookieNameOfItsInstance_IsTrue()
    {
        const string DataFolder = @"D:\Demo\data";
        await using var server = await KilerServer.StartAsync(new StartOptions { DataFolder = DataFolder });
        using var health = new HealthCheck(InstancePlace.For(DataFolder, @"C:\Local\Kiler", 1).CookieName);

        Assert.True(await health.AnswersAsync(new RunningInstance(server.Port, server.Token), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Answers_WithTheCookieNameOfAnotherInstance_IsFalse()
    {
        await using var server = await KilerServer.StartAsync(new StartOptions { DataFolder = @"D:\Demo\data" });
        using var health = new HealthCheck(InstancePlace.CookieNameFor(null));

        Assert.False(await health.AnswersAsync(new RunningInstance(server.Port, server.Token), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Answers_WithAnotherToken_IsFalse()
    {
        await using var server = await KilerServer.StartAsync();
        using var health = new HealthCheck(server.CookieName);

        Assert.False(await health.AnswersAsync(new RunningInstance(server.Port, new AccessToken()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Answers_WhenNothingListens_IsFalse()
    {
        using var health = new HealthCheck(AnyCookie);

        Assert.False(await health.AnswersAsync(new RunningInstance(ClosedPort(), new AccessToken()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Answers_WhenTheServerDoesNotRespond_IsFalseAfterTheTimeout()
    {
        // The connection is accepted by the system, but nothing ever responds.
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            using var health = new HealthCheck(AnyCookie);

            Assert.False(await health.AnswersAsync(
                new RunningInstance(((IPEndPoint)silent.LocalEndpoint).Port, new AccessToken()),
                TestContext.Current.CancellationToken));
        }
        finally
        {
            silent.Stop();
        }
    }

    [Fact]
    public async Task Answers_ARedirect_IsFalseAndIsNotFollowed()
    {
        // A redirect would carry the request, and with it the cookie of the token, to another port.
        var target = new TcpListener(IPAddress.Loopback, 0);
        var redirecting = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        redirecting.Start();
        try
        {
            var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
            var answer = AnswerOnceAsync(redirecting, $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{targetPort}/api/health\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            using var health = new HealthCheck(AnyCookie);

            Assert.False(await health.AnswersAsync(
                new RunningInstance(((IPEndPoint)redirecting.LocalEndpoint).Port, new AccessToken()),
                TestContext.Current.CancellationToken));

            await answer;
            Assert.False(target.Pending());
        }
        finally
        {
            target.Stop();
            redirecting.Stop();
        }
    }

    [Fact]
    public async Task Answers_WhenTheCallerCancels_Throws()
    {
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            using var health = new HealthCheck(AnyCookie);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => health.AnswersAsync(
                new RunningInstance(((IPEndPoint)silent.LocalEndpoint).Port, new AccessToken()),
                cancel.Token));
        }
        finally
        {
            silent.Stop();
        }
    }

    /// <summary>Accepts one connection, reads the request head and sends <paramref name="response"/>.</summary>
    private static async Task AnswerOnceAsync(TcpListener listener, string response)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = client.GetStream();
        var buffer = new byte[8192];
        var length = 0;
        while (!Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

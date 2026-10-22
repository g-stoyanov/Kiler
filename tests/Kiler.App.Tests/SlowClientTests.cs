using System.Net;
using System.Net.Sockets;

namespace Kiler.App.Tests;

/// <summary>A client that holds a request open: the stop must not wait for it longer than a second start waits.</summary>
public class SlowClientTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void ShutdownTimeout_IsShorterThanTheWaitOfASecondStart()
    {
        Assert.True(KilerHost.ShutdownTimeout < SingleInstance.Wait);
    }

    [Fact]
    public async Task StopWithAHalfSentRequestWithoutTheToken_EndsBeforeASecondStartGivesUp()
    {
        // The guard sees only whole requests, so it cannot turn this one away. Without a limit, Kestrel waits 30 s for it.
        await using var server = await KilerServer.StartAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port, Cancel);
        await tcp.GetStream().WriteAsync("GET /api/health HTTP/1.1\r\nHo"u8.ToArray(), Cancel);
        await Task.Delay(TimeSpan.FromMilliseconds(200), Cancel);

        await server.StopAsync().WaitAsync(SingleInstance.Wait, Cancel);
    }
}

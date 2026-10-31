using System.Text;

namespace Kiler.App.Tests;

/// <summary>
/// An open <c>/api/events</c> stream, read one event at a time as the exact text on the wire. Its own client, so that
/// disposing it closes the connection, as a page that closes does.
/// </summary>
internal sealed class EventStream : IDisposable
{
    /// <summary>The longest a test waits for one event.</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _client;
    private readonly HttpResponseMessage _response;
    private readonly Stream _body;

    private EventStream(HttpClient client, HttpResponseMessage response, Stream body)
    {
        _client = client;
        _response = response;
        _body = body;
    }

    public HttpResponseMessage Response => _response;

    /// <summary>Opens the stream with the cookie and reads the first event, so the server has counted the connection.</summary>
    public static Task<EventStream> OpenAsync(KilerServer server) => OpenAsync(server.CreateSignedInClient());

    /// <summary>The same with a client that has the address and the cookie, such as one for the published exe. The stream owns the client.</summary>
    public static async Task<EventStream> OpenAsync(HttpClient client)
    {
        try
        {
            var response = await client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            var stream = new EventStream(client, response, await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
            Assert.Equal("event: ping\ndata: \n\n", await stream.ReadEventAsync());
            return stream;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>The text of the next event up to and with its blank line; null when the stream has ended.</summary>
    public async Task<string?> ReadEventAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(ReadTimeout);
        var text = new StringBuilder();
        var one = new byte[1];
        while (!text.ToString().EndsWith("\n\n", StringComparison.Ordinal))
        {
            if (await _body.ReadAsync(one, timeout.Token) == 0)
            {
                return text.Length == 0 ? null : throw new InvalidDataException($"The stream ended inside an event: {text}");
            }

            text.Append((char)one[0]);
        }

        return text.ToString();
    }

    /// <summary>The next event that is not a <c>ping</c>: a fake clock gives a ping with each long step, the real one every interval.</summary>
    public async Task<string?> ReadAfterPingsAsync()
    {
        while (true)
        {
            var next = await ReadEventAsync();
            if (next != "event: ping\ndata: \n\n")
            {
                return next;
            }
        }
    }

    public void Dispose()
    {
        _body.Dispose();
        _response.Dispose();
        _client.Dispose();
    }
}

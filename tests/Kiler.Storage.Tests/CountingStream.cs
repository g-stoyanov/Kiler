namespace Kiler.Storage.Tests;

/// <summary>
/// A stream to read that reports every read to the test. <paramref name="length"/> puts another size in place of the real
/// one, as a file that grows or shrinks while it is read shows it. <paramref name="maxChunk"/> limits the bytes of one
/// read, as a network folder can give fewer bytes than asked. <paramref name="onDispose"/> is called once, when the stream is
/// disposed.
/// </summary>
internal sealed class CountingStream(Stream inner, Action<int> onRead, long? length = null, int maxChunk = int.MaxValue, Action? onDispose = null) : Stream
{
    private Action? _onDispose = onDispose;

    /// <summary>The stream it reports the reads of, such as the file that <see cref="IFileSystem.OpenRead"/> opened.</summary>
    public Stream Inner => inner;

    public override bool CanRead => true;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => length ?? inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer[..Math.Min(buffer.Length, maxChunk)]);
        onRead(read);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            Interlocked.Exchange(ref _onDispose, null)?.Invoke();
        }

        base.Dispose(disposing);
    }
}

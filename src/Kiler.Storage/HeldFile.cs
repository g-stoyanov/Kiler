using Microsoft.Win32.SafeHandles;

namespace Kiler.Storage;

/// <summary>
/// A file held open, as itself, to be read and then, maybe, deleted through its handle (M2.4): the delete takes the file
/// that was read, whatever is at its name by then. No one else can open it to write or delete it while it is held.
/// </summary>
internal sealed class HeldFile(string path, FileStream stream, DateTimeOffset lastWriteTime) : IDisposable
{
    public string Path { get; } = path;

    /// <summary>The bytes of the file, from the start.</summary>
    public Stream Content => stream;

    /// <summary>The write time when it was opened, through its handle, on the clock of the file server.</summary>
    public DateTimeOffset LastWriteTime { get; } = lastWriteTime;

    public SafeFileHandle Handle => stream.SafeFileHandle;

    public void Dispose() => stream.Dispose();
}

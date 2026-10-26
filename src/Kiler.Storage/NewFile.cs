using Microsoft.Win32.SafeHandles;

namespace Kiler.Storage;

/// <summary>
/// A file that the layer has just made and written, still open (M2.3). The handle follows the file through its rename, so
/// the times are set on the file that was written, whatever is at its path by then.
/// </summary>
internal sealed class NewFile(string path, FileStream stream) : IDisposable
{
    /// <summary>Where the file was made. A rename takes it elsewhere; the handle follows it.</summary>
    public string Path { get; } = path;

    public SafeFileHandle Handle => stream.SafeFileHandle;

    public void Dispose() => stream.Dispose();
}

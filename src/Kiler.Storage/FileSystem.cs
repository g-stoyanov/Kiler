using System.IO.Enumeration;
using Microsoft.Win32.SafeHandles;

namespace Kiler.Storage;

/// <summary>The file operations of the data layer on System.IO.</summary>
internal sealed class FileSystem : IFileSystem
{
    /// <summary>Every entry: the defaults skip hidden and system ones, and anyone can set those on a file in the shared folder.</summary>
    private static readonly EnumerationOptions AllEntries = new() { AttributesToSkip = 0, IgnoreInaccessible = false };

    private FileSystem()
    {
    }

    public static FileSystem Instance { get; } = new();

    public FileAttributes? Attributes(string path)
    {
        try
        {
            // GetFileAttributesEx, which gives a link as itself.
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public IReadOnlyList<FileEntry> List(string folder) =>
    [
        .. new FileSystemEnumerable<FileEntry>(
            folder,
            static (ref entry) => new FileEntry(entry.FileName.ToString(), entry.Attributes, entry.Length, entry.CreationTimeUtc, entry.LastWriteTimeUtc),
            AllEntries),
    ];

    // Not Directory.CreateDirectory: it makes every missing folder above, and a data folder that has gone must not come
    // back, and it may follow a link at the name (M2.5).
    public void CreateFolder(string path) => NativeFiles.OpenOrCreateFolder(path).Dispose();

    // MoveFile without MOVEFILE_REPLACE_EXISTING, a rename that SMB does in one step.
    public void MoveFolder(string from, string to) => Directory.Move(from, to);

    public PinnedFolder HoldFolder(string path) => PinnedFolder.Open(path);

    public PinnedFolder HoldOrCreateFolder(string path) => PinnedFolder.OpenOrCreate(path);

    public Stream? OpenRead(string path)
    {
        if (NativeFiles.OpenFileToRead(path) is not { } handle)
        {
            return null;
        }

        try
        {
            // No buffer of its own: the reader reads the whole file into one buffer.
            return new FileStream(handle, FileAccess.Read, bufferSize: 0);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public FileStamp Stamp(Stream file) =>
        file is FileStream { SafeFileHandle: var handle } ? StampOf(handle) : throw new ArgumentException("Not a file that OpenRead opened.", nameof(file));

    public FileTimes? OpenTimes(string path)
    {
        SafeFileHandle? handle;
        var canSet = true;
        try
        {
            handle = NativeFiles.OpenFileForTimes(path, toSet: true);
        }
        catch (UnauthorizedAccessException)
        {
            // Times that may not be set, or a folder, which the second open refuses too.
            handle = NativeFiles.OpenFileForTimes(path, toSet: false);
            canSet = false;
        }

        if (handle is null)
        {
            return null;
        }

        try
        {
            return new FileTimes(path, handle, StampOf(handle), NativeFiles.Id(handle), canSet);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void KeepCreationTime(FileTimes file)
    {
        if (file.CanSet)
        {
            File.SetCreationTimeUtc(file.Handle, file.Stamp.CreationTime.UtcDateTime);
        }
    }

    public NewFile? CreateFile(string path, ReadOnlySpan<byte> bytes)
    {
        // Read access, too, for the times and the size; read sharing, as the rename makes it the document.
        if (NativeFiles.CreateNewFile(path) is not { } handle)
        {
            return null;
        }

        FileStream stream;
        try
        {
            stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0);
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        try
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return new NewFile(path, stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public FileStamp SetCreationToWriteTime(NewFile file)
    {
        var written = File.GetLastWriteTimeUtc(file.Handle);

        // A write time set through the handle is not moved again by the file system, also not when the file is closed.
        File.SetLastWriteTimeUtc(file.Handle, written);
        File.SetCreationTimeUtc(file.Handle, written);
        return new FileStamp(RandomAccess.GetLength(file.Handle), written, written);
    }

    // MoveFileEx, with MOVEFILE_REPLACE_EXISTING when it replaces: a rename that SMB does in one step. Not File.Move, which
    // would copy to another volume (M2.5).
    public bool MoveFile(string from, string to, bool replace)
    {
        try
        {
            NativeFiles.MoveFile(from, to, replace);
            return true;
        }
        catch (IOException ex) when (!replace && IsNameTaken(ex))
        {
            return false;
        }
    }

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteNewFile(NewFile file) => NativeFiles.DeleteOnClose(file.Handle);

    public IDisposable? CreateMarker(string path) => NativeFiles.CreateMarkerFile(path);

    public HeldFile? HoldFile(string path)
    {
        if (NativeFiles.OpenFileToDelete(path) is not { } handle)
        {
            return null;
        }

        FileStream stream;
        try
        {
            stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        try
        {
            return new HeldFile(path, stream, File.GetLastWriteTimeUtc(handle));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void DeleteHeldFile(HeldFile file) => NativeFiles.DeleteOnClose(file.Handle);

    public void Wait(TimeSpan time) => Thread.Sleep(time);

    private static FileStamp StampOf(SafeFileHandle handle) =>
        new(RandomAccess.GetLength(handle), File.GetCreationTimeUtc(handle), File.GetLastWriteTimeUtc(handle));

    /// <summary>ERROR_ALREADY_EXISTS from a rename (measured in M2.3), or ERROR_FILE_EXISTS, which a file server may give.</summary>
    private static bool IsNameTaken(IOException ex) => ex.HResult is unchecked((int)0x80070050) or unchecked((int)0x800700B7);
}

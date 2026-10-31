using System.IO.Enumeration;
using Microsoft.Win32.SafeHandles;

namespace Kiler.Storage;

/// <summary>
/// The file operations of the data layer on System.IO, with the owners from <paramref name="owners"/>, read through the
/// handles (M2.6): Kiler.exe gives the owners of Windows, the tests a fake.
/// </summary>
internal sealed class FileSystem(IFileOwner owners) : IFileSystem
{
    /// <summary>Every entry: the defaults skip hidden and system ones, and anyone can set those on a file in the shared folder.</summary>
    private static readonly EnumerationOptions AllEntries = new() { AttributesToSkip = 0, IgnoreInaccessible = false };

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

    public IReadOnlyList<FileEntry> List(string folder, int limit = int.MaxValue) =>
        [.. new FileSystemEnumerable<FileEntry>(folder, Entry, AllEntries).Take(limit)];

    // Not Directory.CreateDirectory: it makes every missing folder above, and a data folder that has gone must not come
    // back, and it may follow a link at the name (M2.5).
    public void CreateFolder(string path) => NativeFiles.OpenOrCreateFolder(path).Dispose();

    // MoveFile without MOVEFILE_REPLACE_EXISTING, a rename that SMB does in one step.
    public void MoveFolder(string from, string to) => Directory.Move(from, to);

    public PinnedFolder HoldFolder(string path, bool readOwner = false) => PinnedFolder.Open(path, readOwner);

    public PinnedFolder HoldOrCreateFolder(string path, bool readOwner = false) => PinnedFolder.OpenOrCreate(path, readOwner);

    public PinnedFolder? HoldNewFolder(string path, bool readOwner = false) => PinnedFolder.CreateNew(path, readOwner);

    public FileId Id(PinnedFolder folder) => NativeFiles.Id(folder.Handle);

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

    public string? Owner(Stream file) =>
        file is FileStream { SafeFileHandle: var handle } ? owners.Of(handle) : throw new ArgumentException("Not a file that OpenRead opened.", nameof(file));

    public string? Owner(PinnedFolder folder) => owners.Of(folder.Handle);

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
        // Anyone who sees the new file on the share can set its times before this read.
        var written = TimeOf(File.GetLastWriteTimeUtc, file.Handle);

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
        catch (UnauthorizedAccessException) when (!replace && Attributes(to) is not null)
        {
            // A file server may refuse a rename onto a name that an open file takes, or a file deleted while others hold it,
            // with access denied rather than "exists" (measured on the shared folder in M2.11). The name is taken all the same.
            // A name that cannot be looked at throws in the filter, which counts as not taken, so the denial stays.
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
            return new HeldFile(path, stream, TimeOf(File.GetLastWriteTimeUtc, handle));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void DeleteHeldFile(HeldFile file) => NativeFiles.DeleteOnClose(file.Handle);

    public PinnedFolder HoldFolderToDelete(string path) => PinnedFolder.OpenToDelete(path);

    public void DeleteHeldFolder(PinnedFolder folder) => NativeFiles.DeleteOnClose(folder.Handle);

    public SafeFileHandle OpenToAppend(string path) => NativeFiles.OpenFileToAppend(path);

    public void Append(SafeFileHandle file, ReadOnlySpan<byte> bytes) => NativeFiles.AppendToEnd(file, bytes);

    public void Wait(TimeSpan time) => Thread.Sleep(time);

    private static FileStamp StampOf(SafeFileHandle handle) =>
        new(RandomAccess.GetLength(handle), TimeOf(File.GetCreationTimeUtc, handle), TimeOf(File.GetLastWriteTimeUtc, handle));

    /// <summary>
    /// An entry of a listing. Anyone on the share can give a file a time after the year 9999, which Windows takes and a
    /// <see cref="DateTime"/> cannot hold; such an entry comes with its times unread, so it does not stop the listing (third
    /// security review of M2.7).
    /// </summary>
    private static FileEntry Entry(ref FileSystemEntry entry)
    {
        var name = entry.FileName.ToString();
        try
        {
            return new FileEntry(name, entry.Attributes, entry.Length, entry.CreationTimeUtc, entry.LastWriteTimeUtc);
        }
        catch (ArgumentOutOfRangeException)
        {
            return new FileEntry(name, entry.Attributes, entry.Length, default, default, TimesUnread: true);
        }
    }

    /// <summary>A time of a file through its handle; one after the year 9999 is an <see cref="IOException"/>, as <see cref="Entry"/> says.</summary>
    private static DateTime TimeOf(Func<SafeFileHandle, DateTime> read, SafeFileHandle handle)
    {
        try
        {
            return read(handle);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new IOException("The file has a time after the year 9999, which Kiler cannot read.", ex);
        }
    }

    /// <summary>ERROR_ALREADY_EXISTS from a rename (measured in M2.3), or ERROR_FILE_EXISTS, which a file server may give.</summary>
    private static bool IsNameTaken(IOException ex) => ex.HResult is unchecked((int)0x80070050) or unchecked((int)0x800700B7);
}

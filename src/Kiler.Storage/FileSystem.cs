using System.IO.Enumeration;

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

    public void CreateFolder(string path)
    {
        // Directory.CreateDirectory also makes every missing folder above, and a data folder that has gone must not come back.
        var above = Path.GetDirectoryName(path);
        if (above is null || !Directory.Exists(above))
        {
            throw new DirectoryNotFoundException($"The folder above it does not exist: {path}");
        }

        // CreateDirectory takes anything with the folder attribute that is there already, a junction too.
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
        {
            throw new IOException($"A link is where a folder should be made: {path}");
        }
    }

    // MoveFile without MOVEFILE_REPLACE_EXISTING, a rename that SMB does in one step.
    public void MoveFolder(string from, string to) => Directory.Move(from, to);

    public PinnedFolder HoldFolder(string path) => PinnedFolder.Open(path);

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

    public NewFile? CreateFile(string path, ReadOnlySpan<byte> bytes)
    {
        FileStream stream;
        try
        {
            // CreateNew never opens what is there, a link neither, also one whose target is missing (measured in M2.3). Read
            // access, too, for the times and the size (FILE_READ_ATTRIBUTES). Read sharing, as the rename makes it the document.
            stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.Read | FileShare.Delete,
                BufferSize = 0,
            });
        }
        catch (IOException ex) when (IsNameTaken(ex))
        {
            return null;
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

    // MoveFileEx, with MOVEFILE_REPLACE_EXISTING when it replaces: a rename that SMB does in one step.
    public bool MoveFile(string from, string to, bool replace)
    {
        try
        {
            File.Move(from, to, replace);
            return true;
        }
        catch (IOException ex) when (!replace && IsNameTaken(ex))
        {
            return false;
        }
    }

    public void DeleteFile(string path) => File.Delete(path);

    public void Wait(TimeSpan time) => Thread.Sleep(time);

    /// <summary>ERROR_FILE_EXISTS from a create, ERROR_ALREADY_EXISTS from a rename (measured in M2.3).</summary>
    private static bool IsNameTaken(IOException ex) => ex.HResult is unchecked((int)0x80070050) or unchecked((int)0x800700B7);
}

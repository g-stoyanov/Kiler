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

    public Stream? OpenRead(string path)
    {
        try
        {
            // No buffer of its own: the reader reads the whole file into one buffer.
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = 0,
            });
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}

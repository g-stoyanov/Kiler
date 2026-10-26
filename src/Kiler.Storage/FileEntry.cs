namespace Kiler.Storage;

/// <summary>One entry of a folder listing, with what the listing itself gives, so it costs no request of its own.</summary>
internal readonly record struct FileEntry(string Name, FileAttributes Attributes, long Size, DateTimeOffset CreationTime, DateTimeOffset LastWriteTime)
{
    public bool IsFolder => (Attributes & FileAttributes.Directory) != 0;

    /// <summary>A reparse point: a junction, a symbolic link or another redirection. The layer never follows one.</summary>
    public bool IsLink => (Attributes & FileAttributes.ReparsePoint) != 0;
}

namespace Kiler.Storage;

/// <summary>
/// One entry of a folder listing, with what the listing itself gives, so it costs no request of its own.
/// <see cref="TimesUnread"/>: a time after the year 9999, which a <see cref="DateTime"/> cannot hold, so the times are not
/// given (M2.7).
/// </summary>
internal readonly record struct FileEntry(
    string Name, FileAttributes Attributes, long Size, DateTimeOffset CreationTime, DateTimeOffset LastWriteTime, bool TimesUnread = false)
{
    public bool IsFolder => (Attributes & FileAttributes.Directory) != 0;

    /// <summary>A reparse point: a junction, a symbolic link or another redirection. The layer never follows one.</summary>
    public bool IsLink => (Attributes & FileAttributes.ReparsePoint) != 0;

    public FileStamp Stamp => new(Size, CreationTime, LastWriteTime);
}

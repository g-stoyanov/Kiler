namespace Kiler.Storage;

/// <summary>
/// The size and the times of a document file, as a listing or the handle of a write gives them (M2.3). Times in UTC, from
/// the clock of the file server.
/// </summary>
public readonly record struct FileStamp(long Size, DateTimeOffset CreationTime, DateTimeOffset LastWriteTime)
{
    /// <summary>
    /// Written after it was made. Kiler makes every file anew and sets its creation time to its write time, so a later write
    /// time means it was edited in place, outside Kiler, as Notepad does (design 5.4 and 7.8). For a moment after a write of
    /// Kiler it looks so too; <see cref="InPlaceEdits"/> waits that out before it reports a file.
    /// </summary>
    public bool LooksEditedInPlace => LastWriteTime > CreationTime;
}

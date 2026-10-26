namespace Kiler.Storage;

/// <summary>How a write of a document ended (M2.3). Only <see cref="Written"/> changed the document.</summary>
public enum WriteOutcome
{
    /// <summary>The document has its new version.</summary>
    Written,

    /// <summary>Creating: a document with that id exists. Nothing was written.</summary>
    Exists,

    /// <summary>Replacing: there is no document to replace. Nothing was written.</summary>
    Missing,

    /// <summary>Replacing: the file on disk is damaged, so its version is unknown. Nothing was written; M2.9 restores it.</summary>
    Damaged,

    /// <summary>
    /// Replacing: the version on disk is not the one the write changes, or another write of it is under way, as its history
    /// slot shows (M2.4). Nothing was written.
    /// </summary>
    Conflict,

    /// <summary>
    /// Replacing: the version on disk is <see cref="int.MaxValue"/>, which has no version after it. Anyone on the shared
    /// folder can set it. Nothing was written.
    /// </summary>
    NoNextVersion,

    /// <summary>
    /// Replacing: the file stayed open in another program, such as the read of another Kiler or an antivirus, or it is
    /// read-only, so it could not be renamed over. Nothing was written; the write can be tried again.
    /// </summary>
    Busy,
}

/// <summary>The outcome of a write, with what the caller needs of it.</summary>
public sealed class WriteResult
{
    private WriteResult(WriteOutcome outcome, int version, FileStamp? stamp, Document? onDisk, DocumentDamage? damage)
    {
        Outcome = outcome;
        Version = version;
        Stamp = stamp;
        OnDisk = onDisk;
        Damage = damage;
    }

    public WriteOutcome Outcome { get; }

    /// <summary>The version that was written; 0 when nothing was.</summary>
    public int Version { get; }

    /// <summary>The size and the times of the new file, when it was written.</summary>
    public FileStamp? Stamp { get; }

    /// <summary>The document on disk, at a <see cref="WriteOutcome.Conflict"/>.</summary>
    public Document? OnDisk { get; }

    /// <summary>Why the file on disk is damaged, at <see cref="WriteOutcome.Damaged"/>.</summary>
    public DocumentDamage? Damage { get; }

    internal static WriteResult Written(int version, FileStamp stamp) => new(WriteOutcome.Written, version, stamp, null, null);

    internal static WriteResult Conflict(Document onDisk) => new(WriteOutcome.Conflict, 0, null, onDisk, null);

    internal static WriteResult Damaged(DocumentDamage damage) => new(WriteOutcome.Damaged, 0, null, null, damage);

    internal static WriteResult Not(WriteOutcome outcome) => new(outcome, 0, null, null, null);
}

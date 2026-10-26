namespace Kiler.Storage;

/// <summary>
/// How a write of a document ended (M2.3): a create, a replace, a delete or a restore from the trash (M2.5). Only
/// <see cref="Written"/> and <see cref="Deleted"/> changed the document.
/// </summary>
public enum WriteOutcome
{
    /// <summary>The document has its new version, or, restored from the trash, its version from there.</summary>
    Written,

    /// <summary>
    /// Creating: a document with that id exists, or did: its history is there, as a delete leaves it (M2.5), and a new
    /// version 1 would meet the old slot of version 1 and could never be written again. Restoring from the trash: a document
    /// with that id exists. Nothing was written.
    /// </summary>
    Exists,

    /// <summary>
    /// Replacing or deleting: there is no document. Restoring from the trash: there is no copy of it in the trash. Nothing
    /// was written.
    /// </summary>
    Missing,

    /// <summary>
    /// Replacing or deleting: the file on disk is damaged, so its version is unknown; restoring from the trash: the copy
    /// there is damaged. Nothing was written; M2.9 restores it.
    /// </summary>
    Damaged,

    /// <summary>
    /// Replacing or deleting: the version on disk is not the one the write changes, or its history slot is there (M2.4):
    /// another write of that version is under way, a crash left the slot less than two minutes ago, or the slot is not a
    /// leftover that can be taken over, such as one with other bytes, which stays in the way until it is removed. Nothing was
    /// written. <see cref="WriteResult.OnDisk"/> is the document as the write read it. When its version is the one the write
    /// changes, the slot was in the way, and the other write may replace the document a moment later: a write of the same
    /// version again meets the same slot, so an upper layer does not repeat it in a loop.
    /// </summary>
    Conflict,

    /// <summary>
    /// Replacing: the version on disk is <see cref="int.MaxValue"/>, which has no version after it. Anyone on the shared
    /// folder can set it. Nothing was written.
    /// </summary>
    NoNextVersion,

    /// <summary>
    /// Replacing, deleting or restoring from the trash: a file stayed open in another program, such as the read of another
    /// Kiler or an antivirus, or it is read-only, so it could not be renamed over or moved. Nothing was written; the write can
    /// be tried again.
    /// </summary>
    Busy,

    /// <summary>The document is in the trash, and its version in its history slot (M2.5).</summary>
    Deleted,
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

    /// <summary>The version that was written, restored or deleted; 0 when nothing was.</summary>
    public int Version { get; }

    /// <summary>The size and the times of the new file when it was written, or of the file restored from the trash.</summary>
    public FileStamp? Stamp { get; }

    /// <summary>The document on disk, at a <see cref="WriteOutcome.Conflict"/>.</summary>
    public Document? OnDisk { get; }

    /// <summary>Why the file on disk is damaged, at <see cref="WriteOutcome.Damaged"/>.</summary>
    public DocumentDamage? Damage { get; }

    internal static WriteResult Written(int version, FileStamp stamp) => new(WriteOutcome.Written, version, stamp, null, null);

    internal static WriteResult Deleted(int version) => new(WriteOutcome.Deleted, version, null, null, null);

    internal static WriteResult Conflict(Document onDisk) => new(WriteOutcome.Conflict, 0, null, onDisk, null);

    internal static WriteResult Damaged(DocumentDamage damage) => new(WriteOutcome.Damaged, 0, null, null, damage);

    internal static WriteResult Not(WriteOutcome outcome) => new(outcome, 0, null, null, null);
}

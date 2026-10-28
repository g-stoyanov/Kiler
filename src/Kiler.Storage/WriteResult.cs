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
    /// with that id exists. Writing back from the local copy (M2.9a): anything is at its name, or a document of its id is in
    /// the other place, its folder or the trash, as Kiler never makes two documents of one id. Nothing was written.
    /// </summary>
    Exists,

    /// <summary>
    /// Replacing or deleting: there is no document. Restoring from the trash: there is no copy of it in the trash. Writing
    /// back from the local copy (M2.9a): the copy no longer has the last good bytes, as their file has gone or has other bytes
    /// than its entry; the change stays listed until it is forgotten. Nothing was written.
    /// </summary>
    Missing,

    /// <summary>
    /// Replacing or deleting: the file on disk is damaged, so its version is unknown; restoring from the trash: the copy
    /// there is damaged. Nothing was written; a write back from the local copy restores it (<see cref="DataCopy.WriteBack"/>,
    /// M2.9b).
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
    /// Kiler or an antivirus, or it is read-only, so it could not be renamed over or moved. Writing back from the local copy
    /// (M2.9a): the last good bytes in the copy cannot be read now, as another program holds their file. Writing back over a
    /// file that is there (M2.9b): that file is held open without sharing the delete, or is read-only; a write of another
    /// Kiler of its version is under way, or left its slot less than two minutes ago; or the copy has lost its bytes of that
    /// file, which the next load reads again. Nothing was written; the write can be tried again.
    /// </summary>
    Busy,

    /// <summary>The document is in the trash, and its version in its history slot (M2.5).</summary>
    Deleted,

    /// <summary>
    /// A write through the local copy while it is offline (<see cref="DataCopy.Offline"/>, M2.8a): the last load could not
    /// reach the share, so the write was refused before it touched it (decided in M2.8a, by the user). Nothing was written;
    /// the write can be tried again once the share is back.
    /// </summary>
    Offline,

    /// <summary>
    /// Writing back from the local copy (<see cref="DataCopy.WriteBack"/>, M2.9a): the change is no longer in the copy as the
    /// list gave it (<see cref="DataCopy.ToRestore"/>), as a load has seen the document since, or it was forgotten. Over a file
    /// that is there (M2.9b): that file is no longer the one that the list gave, as it has gone, another entry is at its name,
    /// or it has changed since, such as by a new version that another Kiler wrote. Nothing was written; the list is read again.
    /// The one exception: the name of the file there, which the write back deleted, stayed taken for all the tries of its
    /// rename, so the document is gone until a write back of a gone document brings it back, once a load has reported it.
    /// </summary>
    NotListed,

    /// <summary>
    /// Writing back from the local copy (M2.9a, M2.9b): the history of the share has a version after the one written back, or
    /// one of it with other bytes, so other Kilers wrote versions that this copy did not see, and the document back at that
    /// version would block their writes for good (M2.4). Nothing was written, and the history stays as it is: the newer
    /// versions are in it, and in the copies that saw them (decided in M2.9a, by the user).
    /// </summary>
    Behind,

    /// <summary>
    /// Writing back from the local copy into the private folder of another user (M2.9c): a folder that the write back needs
    /// is missing there, <c>records\</c>, the folder of the trash, or, over a good document at a version that is not before
    /// the one written back, the history folder of the document, which a document never changed does not have. A write back
    /// makes no folder in the private folder of another user, as one would have the SID of the admin, and the Kiler of the
    /// owner would refuse it for good (decided by the user). Nothing was written. The folder comes from the Kiler of the
    /// owner, as the owner: such as by a write back of the same change from the owner's own copy, which reports it too
    /// (M2.9a, M2.9b), or a later write of the owner there; or the change is forgotten.
    /// </summary>
    NoFolder,
}

/// <summary>The outcome of a write, with what the caller needs of it.</summary>
public sealed class WriteResult
{
    private WriteResult(WriteOutcome outcome, int version, FileStamp? stamp, Document? onDisk, DocumentDamage? damage, byte[]? bytes = null, byte[]? old = null)
    {
        Outcome = outcome;
        Version = version;
        Stamp = stamp;
        OnDisk = onDisk;
        Damage = damage;
        Bytes = bytes;
        Old = old;
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

    /// <summary>
    /// The bytes of the file that was written, or restored from the trash, for the local copy, which keeps the bytes that this
    /// Kiler wrote and not the stamp of the share (M2.7a). They belong to the layer.
    /// </summary>
    internal byte[]? Bytes { get; }

    /// <summary>The bytes that a replace or a delete put into the history slot of the version it changed, for the local copy (M2.7a).</summary>
    internal byte[]? Old { get; }

    internal static WriteResult Written(int version, FileStamp stamp, byte[] bytes, byte[]? old = null) =>
        new(WriteOutcome.Written, version, stamp, null, null, bytes, old);

    internal static WriteResult Deleted(int version, byte[] old) => new(WriteOutcome.Deleted, version, null, null, null, old: old);

    internal static WriteResult Conflict(Document onDisk) => new(WriteOutcome.Conflict, 0, null, onDisk, null);

    internal static WriteResult Damaged(DocumentDamage damage) => new(WriteOutcome.Damaged, 0, null, null, damage);

    internal static WriteResult Not(WriteOutcome outcome) => new(outcome, 0, null, null, null);
}

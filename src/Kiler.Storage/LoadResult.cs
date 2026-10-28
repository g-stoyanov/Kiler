namespace Kiler.Storage;

/// <summary>What one load of the share gave (M2.7): the documents that changed, those that have gone, and what was skipped.</summary>
public sealed class LoadResult
{
    internal LoadResult(
        IReadOnlyList<LoadedDocument> changed, IReadOnlyList<DocumentKey> gone, IReadOnlyList<OutsideChange> outside, IReadOnlyList<SkippedEntry> skipped, int moreSkipped, bool trashChanged)
    {
        Changed = changed;
        Gone = gone;
        Outside = outside;
        Skipped = skipped;
        MoreSkipped = moreSkipped;
        TrashChanged = trashChanged;
    }

    /// <summary>
    /// The documents that are new or changed since the local copy last saw them, each read from the share now: a new
    /// <see cref="Document"/>, or why the file is damaged. A document whose bytes could not be written into the copy is here
    /// too, as the share has it (<see cref="SkipReason.NotCopied"/>), and so is one that turned out, once read, to need more
    /// room than the budget has left (<see cref="SkipReason.OverBudget"/>). A document whose times alone changed, read again
    /// with the bytes and the owner that the copy has, is not here, as nothing of it changed (M2.8b); one whose owner changed,
    /// or could not be read, is. The documents of the trash are not here; the
    /// copy gives them (<see cref="DataCopy.Trash"/>). Nor are those of the private folders of other users, which an admin's
    /// copy keeps only for a restore (<see cref="DataCopy.CopyOthers"/>, M2.7c).
    /// </summary>
    public IReadOnlyList<LoadedDocument> Changed { get; }

    /// <summary>
    /// The documents that the local copy had and the share no longer has in their folders, also those moved to the trash; not
    /// those of the private folders of other users (M2.7c).
    /// </summary>
    public IReadOnlyList<DocumentKey> Gone { get; }

    /// <summary>
    /// The documents, in their folders or in the trash, that this load found changed outside Kiler, each once: gone otherwise
    /// than to the trash or back, damaged, edited in place once the edit has settled (<see cref="InPlaceEdits"/>), or at a
    /// version that is not after the last good one. The local copy keeps their last good bytes apart, when it has them, for a
    /// restore (M2.9); the upper layers see the share as it is. Those of the private folders of other users that an admin's copy
    /// is given are here too, under their accounts (<see cref="DataCopy.CopyOthers"/>, M2.7c).
    /// </summary>
    public IReadOnlyList<OutsideChange> Outside { get; }

    /// <summary>
    /// What the load skipped, to be reported: links, names that fail the check, what could not be read, what is over the
    /// budget of the copy, what could not be written into it, a private folder, or a folder in it, that is not the one the
    /// copy knows (M2.7d), and the stopped creates of the private folder that stay (M2.7e). At most 1000 of them;
    /// <see cref="MoreSkipped"/> counts the
    /// rest, as anyone can put any number of names on the share.
    /// </summary>
    public IReadOnlyList<SkippedEntry> Skipped { get; }

    /// <summary>How many skipped entries there were beyond those in <see cref="Skipped"/>.</summary>
    public int MoreSkipped { get; }

    /// <summary>
    /// Whether the documents in the trash that the copy gives (<see cref="DataCopy.Trash"/>) changed in this load: one came,
    /// changed or went, also damaged or moved back (decided in M2.8, by the user), so the upper layers read the trash again
    /// only then. Not those of the private folders of other users, which are never given.
    /// </summary>
    public bool TrashChanged { get; }
}

/// <summary>A document of the local copy or of a load (M2.7): where it is, and what its file gave, with its owner.</summary>
public sealed record LoadedDocument(DocumentKey Key, DocumentRead Read);

/// <summary>
/// An entry of the share that a load skipped (M2.7). <see cref="Name"/> is never the raw name from the share, which anyone
/// can write and which can hold control characters and bidi marks, but its escaped form (<see cref="StorageName.Escape"/>);
/// it can still hold <c>&lt;</c>, <c>&amp;</c>, quotes and braces, so it is shown as text and logged as an argument.
/// </summary>
/// <param name="Folder">
/// The document folder, or <c>null</c> for the root of the data folder; for an entry of the trash or of the history, its
/// folder there, such as <c>trash\records</c>, <c>trash</c> or <c>history\records\8f3a1c</c> (M2.7a); in the private folder of
/// the current user, the same after <c>private\&lt;account&gt;</c>, such as <c>private\demo.user\records</c>, and
/// <c>private\demo.user</c> alone for its root, or for the whole private folder when it cannot be opened (M2.7b); the same
/// for the private folder of another user that an admin's copy is given (M2.7c). <c>private</c> for a stopped create of the
/// private folder of the current user (M2.7e).
/// </param>
/// <param name="Name">
/// The escaped name of the file, or <c>null</c> for the whole folder: one that could not be listed, or one that lists too
/// many entries. With <see cref="SkipReason.NotCopied"/> and no folder, the index of the local copy.
/// </param>
/// <param name="Reason">Why it was skipped.</param>
public readonly record struct SkippedEntry(string? Folder, string? Name, SkipReason Reason);

/// <summary>Why a load skipped an entry of the share.</summary>
public enum SkipReason
{
    /// <summary>A link or another reparse point, which Kiler never follows.</summary>
    Link,

    /// <summary>A <c>.json</c> file whose name fails <see cref="StorageName"/>, so no path is built from it.</summary>
    BadName,

    /// <summary>
    /// A file that could not be opened or read, such as one that someone holds without sharing, or a folder that could not
    /// be listed, such as one that is a link. The next load tries it again.
    /// </summary>
    Unreadable,

    /// <summary>
    /// A document over the budget of the local copy (<see cref="DataCopy.MaxBytes"/>, <see cref="DataCopy.MaxDocuments"/>),
    /// which is not read: a new one is not taken, and a changed one keeps the version the copy has. Or one that turned out,
    /// once read, to need room for its last good bytes too (a version that is not after them), which is given all the same
    /// and keeps the version the copy has (M2.7a). Or a history slot over the budget of the history
    /// (<see cref="DataCopy.MaxHistoryBytes"/>, <see cref="DataCopy.MaxHistorySlots"/>). Or a folder that lists more entries
    /// than a load takes, whose documents stay as the copy has them.
    /// </summary>
    OverBudget,

    /// <summary>
    /// A document read from the share whose bytes could not be written into the local copy, as a full disk or a program
    /// that holds the file of the copy refuses it; it is given all the same, and the next load reads it again. Or the index of
    /// the copy, which the next load writes again.
    /// </summary>
    NotCopied,

    /// <summary>
    /// A private folder, or a folder in it, that is not the one the copy knows (<see cref="ForeignFolderException"/>, M2.7d):
    /// nothing of it is taken, its documents stay as the copy has them, and the writes to it are refused. A private folder
    /// stays so until the folder at the name is accepted (<see cref="DataCopy.AcceptPrivateFolder"/>); a folder in it of
    /// another owner than the private folder, until it is put right on the share.
    /// </summary>
    ForeignFolder,

    /// <summary>
    /// An old temporary folder that a create of the private folder of the current user left in <c>private\</c>, by its name
    /// (<c>&lt;account&gt;.&lt;8 hex digits&gt;.tmp</c>, M2.7e), which the load did not delete: it holds more than the empty
    /// folders of a new private folder, a link among them; it or a folder in it has another owner than the private folder
    /// that the copy knows, or the copy knows none yet; the delete failed; or a link or a file is at the name. Nothing in it
    /// is deleted or followed, and it is never taken for a private folder. The next look tries it again, at most once in 10
    /// minutes, and every load reports what the last look left (M2.8), until it goes or is put right on the share.
    /// </summary>
    StoppedCreate,
}

/// <summary>
/// A document that a load found changed outside Kiler (M2.7a), whose last good bytes the local copy keeps apart for a restore
/// (M2.9).
/// </summary>
/// <param name="Key">The document.</param>
/// <param name="InTrash">Whether it is the copy of the document in the trash.</param>
/// <param name="Reason">What happened to it.</param>
/// <param name="LastGood">The version of the last good bytes that the copy keeps, or <c>null</c> when it has none.</param>
public readonly record struct OutsideChange(DocumentKey Key, bool InTrash, OutsideReason Reason, int? LastGood);

/// <summary>How a document was changed outside Kiler (M2.7a, design 5.3).</summary>
public enum OutsideReason
{
    /// <summary>The share no longer lists it, and it did not go to the trash or come back from there.</summary>
    Gone,

    /// <summary>Its file is damaged (<see cref="DocumentDamage"/>).</summary>
    Damaged,

    /// <summary>Its file was written after it was made (<see cref="FileStamp.LooksEditedInPlace"/>), as Notepad does.</summary>
    EditedInPlace,

    /// <summary>
    /// Its version is lower than that of the last good bytes, or the same with other bytes, which Kiler never writes: a file
    /// put back by hand, a slot moved in from the history, or another file put under its name after a write (decided in
    /// M2.7a, by the user).
    /// </summary>
    OutOfOrder,
}

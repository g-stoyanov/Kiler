namespace Kiler.Storage;

/// <summary>What one load of the share gave (M2.7): the documents that changed, those that have gone, and what was skipped.</summary>
public sealed class LoadResult
{
    internal LoadResult(IReadOnlyList<LoadedDocument> changed, IReadOnlyList<DocumentKey> gone, IReadOnlyList<SkippedEntry> skipped, int moreSkipped)
    {
        Changed = changed;
        Gone = gone;
        Skipped = skipped;
        MoreSkipped = moreSkipped;
    }

    /// <summary>
    /// The documents that are new or changed since the local copy last saw them, each read from the share now: a new
    /// <see cref="Document"/>, or why the file is damaged. A document whose bytes could not be written into the copy is here
    /// too, as the share has it (<see cref="SkipReason.NotCopied"/>).
    /// </summary>
    public IReadOnlyList<LoadedDocument> Changed { get; }

    /// <summary>The documents that the local copy had and the share no longer has.</summary>
    public IReadOnlyList<DocumentKey> Gone { get; }

    /// <summary>
    /// What the load skipped, to be reported: links, names that fail the check, what could not be read, what is over the
    /// budget of the copy, and what could not be written into it. At most 1000 of them; <see cref="MoreSkipped"/> counts the
    /// rest, as anyone can put any number of names on the share.
    /// </summary>
    public IReadOnlyList<SkippedEntry> Skipped { get; }

    /// <summary>How many skipped entries there were beyond those in <see cref="Skipped"/>.</summary>
    public int MoreSkipped { get; }
}

/// <summary>A document of the local copy or of a load (M2.7): where it is, and what its file gave, with its owner.</summary>
public sealed record LoadedDocument(DocumentKey Key, DocumentRead Read);

/// <summary>
/// An entry of the share that a load skipped (M2.7). <see cref="Name"/> is never the raw name from the share, which anyone
/// can write and which can hold control characters and bidi marks, but its escaped form (<see cref="StorageName.Escape"/>);
/// it can still hold <c>&lt;</c>, <c>&amp;</c>, quotes and braces, so it is shown as text and logged as an argument.
/// </summary>
/// <param name="Folder">The document folder, or <c>null</c> for the root of the data folder.</param>
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
    /// which is not read: a new one is not taken, and a changed one keeps the version the copy has. Or a folder that lists
    /// more entries than a load takes, whose documents stay as the copy has them.
    /// </summary>
    OverBudget,

    /// <summary>
    /// A document read from the share whose bytes could not be written into the local copy, as a full disk or a program
    /// that holds the file of the copy refuses it; it is given all the same, and the next load reads it again. Or the index of
    /// the copy, which the next load writes again.
    /// </summary>
    NotCopied,
}

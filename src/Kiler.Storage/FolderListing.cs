namespace Kiler.Storage;

/// <summary>
/// The documents from one listing of a folder, and apart the entries that were skipped to be reported: links, and
/// <c>.json</c> files whose name fails <see cref="StorageName"/>. The temporary files of the layer's writes are given apart
/// too, so the old ones that a crash leaves can be deleted (M2.3). <see cref="Unread"/>: documents whose times could not be
/// read, which are there but cannot be compared (M2.7).
/// </summary>
internal sealed record FolderListing(
    IReadOnlyList<ListedDocument> Documents,
    IReadOnlyList<FileEntry> Links,
    IReadOnlyList<FileEntry> BadNames,
    IReadOnlyList<FileEntry> Temporary,
    IReadOnlyList<ListedDocument> Unread)
{
    /// <summary>The listing of a folder that holds nothing, such as a document folder that has gone (M2.7).</summary>
    public static FolderListing Empty { get; } = new([], [], [], [], []);
}

internal sealed record ListedDocument(DocumentKey Key, FileEntry File);

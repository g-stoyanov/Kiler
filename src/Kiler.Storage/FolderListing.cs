namespace Kiler.Storage;

/// <summary>
/// The documents from one listing of a folder, and apart the entries that were skipped to be reported: links, and
/// <c>.json</c> files whose name fails <see cref="StorageName"/>. The temporary files of the layer's writes are given apart
/// too, so the old ones that a crash leaves can be deleted (M2.3).
/// </summary>
internal sealed record FolderListing(
    IReadOnlyList<ListedDocument> Documents, IReadOnlyList<FileEntry> Links, IReadOnlyList<FileEntry> BadNames, IReadOnlyList<FileEntry> Temporary);

internal sealed record ListedDocument(DocumentKey Key, FileEntry File);

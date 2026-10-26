namespace Kiler.Storage;

/// <summary>
/// The documents from one listing of a folder, and apart the entries that were skipped to be reported: links, and
/// <c>.json</c> files whose name fails <see cref="StorageName"/>.
/// </summary>
internal sealed record FolderListing(IReadOnlyList<ListedDocument> Documents, IReadOnlyList<FileEntry> Links, IReadOnlyList<FileEntry> BadNames);

internal sealed record ListedDocument(DocumentKey Key, FileEntry File);

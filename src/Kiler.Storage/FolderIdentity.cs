namespace Kiler.Storage;

/// <summary>
/// What a private folder is checked against (M2.7d, design 7.8): the owner and the id of its root, read through the handle of
/// the folder when it was made (<see cref="DataFolder.CreatePrivate(string)"/>) or first seen (<see cref="DataFolder.OpenPrivate(string)"/>),
/// and kept in the local copy (decided by the user). The share cannot set it: someone who renames the folder away and puts
/// one of their own at its name, or takes ownership of it, changes the owner, and another folder of the same owner put at the
/// name has another id.
/// </summary>
/// <param name="Owner">The owner of the root, as a SID string (<see cref="IFileOwner"/>).</param>
/// <param name="Id">The id of the root (<see cref="IFileSystem.Id(PinnedFolder)"/>), never one of index 0.</param>
internal sealed record FolderIdentity(string Owner, FileId Id);

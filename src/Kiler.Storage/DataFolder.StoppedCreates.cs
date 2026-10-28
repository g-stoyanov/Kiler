namespace Kiler.Storage;

/// <summary>
/// The creates of a private folder that stopped (M2.7e). <see cref="CreatePrivate(string)"/> makes
/// <c>private\&lt;account&gt;.&lt;8 hex digits&gt;.tmp\</c> with the empty folders of a new private folder and renames it to
/// the name, so a create that stops before the rename, by a crash or a refusal, leaves it. It is never taken for a private
/// folder, as <see cref="StorageName"/> refuses the ending, and the Kiler of the account deletes an old one through the
/// handles of its folders (decided by the user).
/// </summary>
public sealed partial class DataFolder
{
    /// <summary>
    /// Deletes the old temporary folders that the creates of the private folder of <paramref name="account"/> left in
    /// <c>private\</c>: only those of its name (<see cref="TemporaryName"/>), older than <see cref="TemporaryFileAge"/> at
    /// <paramref name="now"/> by the write time of their listing, on the clock of the file server against this machine's, as
    /// the temporary files; a young one can be a create under way. One goes only when it holds nothing but empty folders of a
    /// new private folder, and it and each of them have <paramref name="owner"/>, the owner of the private folder of the
    /// account that the local copy knows (decided by the user). Gives the names of the old ones that it leaves: a folder with
    /// more in it, a link in it, another owner, no owner to compare with, a delete that failed, and a link or a file at such a
    /// name, which is never opened. Each is tried again at the next call. A <c>private\</c> that is not there gives none, and
    /// so does one that lists more than <paramref name="maxEntries"/> entries, as anyone can put any number of names there. A
    /// data folder or a <c>private\</c> that cannot be held or listed is an <see cref="IOException"/> or an
    /// <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    internal IReadOnlyList<string> DeleteStoppedCreates(string account, string? owner, DateTimeOffset now, int maxEntries)
    {
        var privateFolders = PrivateFolders(account);
        using var held = HoldIfThere(Private);
        if (held is null)
        {
            return [];
        }

        var entries = Files.List(privateFolders, maxEntries + 1);

        // An empty private\ made a mount point in place would have given the listing of another folder.
        held.ThrowIfLink();
        if (entries.Count > maxEntries)
        {
            return [];
        }

        var left = new List<string>();
        foreach (var entry in entries)
        {
            // One whose time could not be read is never taken for old.
            if (TemporaryStem(entry.Name) != account || entry.TimesUnread || now - entry.LastWriteTime < TemporaryFileAge)
            {
                continue;
            }

            if (entry.IsLink || !entry.IsFolder || owner is null || !TryDeleteStoppedCreate(Path.Combine(privateFolders, entry.Name), owner, held))
            {
                left.Add(entry.Name);
            }
        }

        return left;
    }

    /// <summary>
    /// Deletes a stopped create (<see cref="DeleteStoppedCreates"/>) when it holds nothing but empty folders of a new private
    /// folder, it and they of <paramref name="owner"/>; <c>false</c> when it is left. It and its folders are held as
    /// themselves to be deleted through their handles (<see cref="IFileSystem.HoldFolderToDelete"/>), and the holds share no
    /// delete, so none of them can be renamed or deleted until the delete is done. All of them are listed and checked through
    /// their handles before anything is deleted, and so are the data folder and <c>private\</c> (<paramref name="above"/>),
    /// which it was opened through by its path: a folder made a mount point in place, whose listing or folders would have been
    /// those of another folder, leaves it whole (security review), and a link in it is never opened. The folders go first,
    /// then the temporary folder, and the file system refuses to delete a folder that is not empty, so anything put in one
    /// after its listing stops the delete there: the folders that went before it were empty.
    /// </summary>
    private bool TryDeleteStoppedCreate(string path, string owner, Holds above)
    {
        try
        {
            using var root = Files.HoldFolderToDelete(path);
            if (Files.Owner(root) != owner)
            {
                return false;
            }

            var entries = Files.List(path, PrivateLayout.Folders.Length + 1);
            if (entries.Count > PrivateLayout.Folders.Length || entries.Any(entry => entry.IsLink || !entry.IsFolder || !PrivateLayout.Folders.Contains(entry.Name)))
            {
                return false;
            }

            var folders = new List<PinnedFolder>();
            try
            {
                foreach (var entry in entries)
                {
                    var folder = Path.Combine(path, entry.Name);
                    var held = Files.HoldFolderToDelete(folder);
                    folders.Add(held);
                    if (Files.Owner(held) != owner || Files.List(folder, 1).Count > 0)
                    {
                        return false;
                    }
                }

                above.ThrowIfLink();
                root.ThrowIfLink();
                foreach (var held in folders)
                {
                    held.ThrowIfLink();
                }

                foreach (var held in folders)
                {
                    Files.DeleteHeldFolder(held);
                }
            }
            finally
            {
                // The folders go when they are let go, before the temporary folder is deleted.
                foreach (var held in folders)
                {
                    held.Dispose();
                }
            }

            Files.DeleteHeldFolder(root);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held by someone, not to be deleted, made a link, or not empty any more: tried again at the next call.
            return false;
        }
    }
}

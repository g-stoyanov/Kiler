namespace Kiler.Storage;

/// <summary>
/// The writes back from the local copy (M2.9a, PLAN M2 step 10): the last good bytes of a document that the copy keeps go
/// back on the share as they are, through a write of their own, as <see cref="CreateDocument"/> refuses an id with a history
/// (M2.5) and a document gone outside the trash keeps its history.
/// </summary>
public sealed partial class DataFolder
{
    /// <summary>
    /// Writes back the last good bytes of a document that went otherwise than to the trash or back, into its folder or, with
    /// <paramref name="inTrash"/>, into the trash, as they are and at their version (decided in M2.9a, by the user): a
    /// temporary file, then a rename that does not replace, as for a create. <see cref="WriteOutcome.Exists"/> when anything
    /// is at its name, or at its name in the other place, as Kiler never makes two documents of one id; the rename decides,
    /// and the looks before it spare the work of one that would fail. <see cref="WriteOutcome.Behind"/> when the history of
    /// the document has a slot (<c>v&lt;N&gt;.json</c>) after their version, or one of their version with other bytes, or
    /// more than <paramref name="maxEntries"/> entries, which cannot be checked: other Kilers wrote versions that the copy
    /// did not see, and the document back at this version would meet those slots and block its writes for good (M2.4). The
    /// slots stay as they are; one of their version with their bytes, as a delete or a crash leaves it, is taken over by the
    /// next write (M2.4), or deleted by a restore from the trash (M2.5). A document folder or a folder of the trash that has
    /// gone with its documents is made again, as itself (M2.5). Bytes that are not a good document of the key are an
    /// <see cref="ArgumentException"/>, before anything is looked at; only their head is read, as an admin's copy never
    /// parses the content of the private folders of other users (M2.7c).
    /// </summary>
    internal WriteResult WriteBack(DocumentKey key, bool inTrash, byte[] bytes, int maxEntries)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var head = DocumentFormat.ReadHead(Known(key), bytes);
        if (head.Damage is not null)
        {
            throw new ArgumentException("Only the bytes of a good document are written back.", nameof(bytes));
        }

        var path = inTrash ? TrashPath(key) : DocumentPath(key);
        using var folders = HoldWriteBackFolders(key, inTrash);
        if (Files.Attributes(path) is not null || Files.Attributes(inTrash ? DocumentPath(key) : TrashPath(key)) is not null)
        {
            return WriteResult.Not(WriteOutcome.Exists);
        }

        var version = head.Version;
        if (IsBehind(key, version, bytes, maxEntries))
        {
            return WriteResult.Not(WriteOutcome.Behind);
        }

        using var file = WriteTemporaryWithTimes(Path.GetDirectoryName(path)!, key.Id, bytes, out _);
        try
        {
            folders.ThrowIfLink();
            if (!Files.MoveFile(file.Path, path, replace: false))
            {
                Discard(file);
                return WriteResult.Not(WriteOutcome.Exists);
            }

            return WriteResult.Written(version, Files.SetCreationToWriteTime(file), bytes);
        }
        catch
        {
            Discard(file);
            throw;
        }
    }

    /// <summary>
    /// The folders that a write back holds: those of the trash, or the document folder, each made again when it has gone, as
    /// the user (M2.9a); the root alone for a document at the root.
    /// </summary>
    private Holds HoldWriteBackFolders(DocumentKey key, bool inTrash)
    {
        if (inTrash)
        {
            return Hold(create: true, TrashFolders(key));
        }

        return key.Folder is { } folder ? Hold(create: true, folder) : Hold(create: false);
    }

    /// <summary>
    /// Whether the history of a document has a slot after <paramref name="version"/>, or one of it without
    /// <paramref name="bytes"/>, or more than <paramref name="maxEntries"/> entries (M2.9a). An entry with the name of such a
    /// slot counts, a link or a folder too; only the form that the layer writes is a slot (<see cref="SlotVersion"/>). A slot
    /// of the version is read through the held folders, opened as itself.
    /// </summary>
    private bool IsBehind(DocumentKey key, int version, byte[] bytes, int maxEntries)
    {
        using var historyFolders = HoldIfThere(HistoryFolders(key));
        if (historyFolders is null)
        {
            return false;
        }

        var entries = Files.List(HistoryFolderPath(key), maxEntries + 1);
        var behind = entries.Count > maxEntries || entries.Any(entry =>
            SlotVersion(entry.Name) is { } slot
            && slot >= version
            && (slot > version || entry.IsLink || entry.IsFolder || !HasBytes(HistoryPath(key, version), bytes)));

        // An empty folder made a mount point in place would have given the listing of another folder, and its slots.
        historyFolders.ThrowIfLink();
        return behind;
    }
}

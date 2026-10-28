namespace Kiler.Storage;

/// <summary>
/// The writes back from the local copy (M2.9a, M2.9b, PLAN M2 step 10): the last good bytes of a document that the copy keeps
/// go back on the share as they are, through a write of their own, as <see cref="CreateDocument"/> refuses an id with a
/// history (M2.5) and a document gone outside the trash keeps its history, and <see cref="ReplaceDocument"/> writes the next
/// version of a good document, never the same one over a file that is damaged or was changed outside Kiler.
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
    /// Writes back the last good bytes of a document over the file at its name, in its folder or, with
    /// <paramref name="inTrash"/>, in the trash (M2.9b): one that is damaged, edited in place or at a version out of order. They
    /// go as they are and at their version, as for a document gone (M2.9a); the version of the file there does not count, as it
    /// is a change outside Kiler, and only the history makes the write <see cref="WriteOutcome.Behind"/> (decided by the user).
    /// The file there is held as itself (<see cref="IFileSystem.HoldFile"/>), so no one can write, rename over, move or delete
    /// it meanwhile, and must still be the one that the copy listed (<paramref name="isListed"/>, given its bytes, or
    /// <c>null</c> for a file over <see cref="DocumentFormat.MaxSize"/>): else, as when it has gone or another entry is at its
    /// name, <see cref="WriteOutcome.NotListed"/>. One held open by someone without sharing the delete, or read-only, is
    /// <see cref="WriteOutcome.Busy"/>. A good document there at a version that is not before theirs is locked as every write
    /// of that version locks it, at the name of its slot (<see cref="TakeLock"/>). Then it is deleted through the hold and is not kept
    /// (decided by the user), and the temporary file is renamed to its name without replacing
    /// (<see cref="MoveIntoFreedName"/>), so a write of another Kiler meanwhile is refused and loses nothing; one at an older
    /// version that the hold refused deletes its own slot. A failure after the delete leaves the document gone, which a load
    /// reports, and which <see cref="WriteBack"/> brings back. Bytes that are not a good document of the key are an
    /// <see cref="ArgumentException"/>, before anything is looked at.
    /// </summary>
    internal WriteResult WriteBackOver(DocumentKey key, bool inTrash, byte[] bytes, int maxEntries, Func<byte[]?, bool> isListed)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(isListed);
        var head = DocumentFormat.ReadHead(Known(key), bytes);
        if (head.Damage is not null)
        {
            throw new ArgumentException("Only the bytes of a good document are written back.", nameof(bytes));
        }

        var path = inTrash ? TrashPath(key) : DocumentPath(key);
        using var folders = HoldIfThere(inTrash ? TrashFolders(key) : FoldersOf(key));

        // A hold, which opens a file as itself, would throw on a link or a folder.
        if (folders is null || (Files.Attributes(path) is { } attributes && (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0))
        {
            return WriteResult.Not(WriteOutcome.NotListed);
        }

        HeldFile? held;
        try
        {
            held = Files.HoldFile(path);
        }
        catch (Exception ex) when (IsHeldOpen(ex))
        {
            return WriteResult.Not(WriteOutcome.Busy);
        }

        if (held is null)
        {
            return WriteResult.Not(WriteOutcome.NotListed);
        }

        using (held)
        {
            var there = DocumentFormat.TryReadBytes(held.Content, out var content) ? content : null;

            // An empty folder made a mount point in place would have given the file of that name elsewhere.
            folders.ThrowIfLink();
            if (!isListed(there))
            {
                return WriteResult.Not(WriteOutcome.NotListed);
            }

            var version = head.Version;
            int? onDisk = !inTrash && there is not null && DocumentFormat.ReadHead(key, there) is { Damage: null } thereHead && thereHead.Version >= version
                ? thereHead.Version
                : null;
            if (IsBehind(key, version, bytes, maxEntries, onDisk == version ? there : null))
            {
                return WriteResult.Not(WriteOutcome.Behind);
            }

            return WriteOver(key, path, version, bytes, onDisk, there, held, folders);
        }
    }

    /// <summary>
    /// The steps of <see cref="WriteBackOver"/> once the file there is checked: the temporary file, the lock, when
    /// <paramref name="onDisk"/> gives the version of the file there (<see cref="TakeLock"/>), the delete of the file there
    /// through its hold, and the rename into its name. The lock goes when it is let go, whatever the end. Only a good document
    /// in its folder at a version that is not before the one written back is locked: a write of such a version by another
    /// Kiler, which the hold refuses and which tries its rename or its move again for about 1.6 s, would otherwise act on the
    /// restored file once the write back is done, and a delete would leave its slot, at or after that version, which blocks the
    /// document (M2.4). One that is damaged is written by no one; one in the trash is only moved back by a restore, which the
    /// hold stops, and which, done after the write back, moves the restored file and refuses itself by the id; and one at an
    /// older version is written by a write that the hold stops, and that deletes its own slot, or, done after the write back,
    /// makes a version that is not after it, which the copies report out of order, or moves the restored file to the trash.
    /// </summary>
    private WriteResult WriteOver(DocumentKey key, string path, int version, byte[] bytes, int? onDisk, byte[]? there, HeldFile held, Holds folders)
    {
        // First, as its write time is the time of the write back on the clock of the file server, against which a leftover
        // slot is old (M2.4).
        using var file = WriteTemporaryWithTimes(Path.GetDirectoryName(path)!, key.Id, bytes, out var written);
        Holds? historyFolders = null;
        IDisposable? lockFile = null;
        try
        {
            if (onDisk is { } locked)
            {
                historyFolders = HoldHistoryFolders(key);
                // The file there is a good document whenever it has a version (WriteBackOver).
                if (!TakeLock(key, locked, bytes, there!, written.LastWriteTime, folders, historyFolders, out lockFile))
                {
                    Discard(file);
                    return WriteResult.Not(WriteOutcome.Busy);
                }
            }

            folders.ThrowIfLink();
            try
            {
                Files.DeleteHeldFile(held);
            }
            catch (Exception ex) when (IsHeldOpen(ex))
            {
                // A read-only file may not be deleted, and stays as it is.
                Discard(file);
                return WriteResult.Not(WriteOutcome.Busy);
            }

            // Its name goes once the hold is let go.
            held.Dispose();
            if (!MoveIntoFreedName(file.Path, path, folders))
            {
                Discard(file);
                return WriteResult.Not(WriteOutcome.NotListed);
            }

            return WriteResult.Written(version, Files.SetCreationToWriteTime(file), bytes);
        }
        catch
        {
            Discard(file);
            throw;
        }
        finally
        {
            lockFile?.Dispose();
            historyFolders?.Dispose();
        }
    }

    /// <summary>
    /// Locks a document whose file there is a good document at <paramref name="onDisk"/>, as every write of that version locks
    /// it (M2.4): a marker at the name of its history slot (<see cref="IFileSystem.CreateMarker"/>), made new, so a write or a
    /// delete of that version by another Kiler finds the name taken, cannot hold the marker to take it over, and is refused.
    /// The marker is deleted when it is let go (<paramref name="lockFile"/>), also when the process ends or the file server
    /// ends its session, so a write back that stops leaves no lock that would block the document (security review of M2.9b).
    /// Only a crash of the machine that holds the files loses the delete, and leaves the marker as an empty file, a dead lock,
    /// which a write back does not count as a version (<see cref="IsBehind"/>; the list of versions shows it as a damaged
    /// slot), and which every write of that version, this one too, takes over once it is old
    /// (<see cref="DeleteLeftoverSlot"/>, second security review). <c>true</c> without a lock when a slot of the version
    /// written back is there with their bytes: no write of that version takes it over (the M2.4 line). A leftover of a crash
    /// at the version written back, with the bytes of the file there, or a dead lock, is taken over as every write of that
    /// version takes it over, once it is <see cref="SlotAge"/> old by <paramref name="now"/>, the write time of the temporary
    /// file of the write back. A slot of a version after the one
    /// written back has made the write back <see cref="WriteOutcome.Behind"/> already, as it can be a version that the copy
    /// did not see, put back over the document (security review of M2.9b), so one there now has come since, and is young.
    /// <c>false</c> when a slot is in the way: a write of that version under way, or a leftover that is not taken over.
    /// </summary>
    private bool TakeLock(DocumentKey key, int onDisk, byte[] bytes, byte[] there, DateTimeOffset now, Holds folders, Holds historyFolders, out IDisposable? lockFile)
    {
        var slot = HistoryPath(key, onDisk);
        folders.ThrowIfLink();
        historyFolders.ThrowIfLink();
        lockFile = Files.CreateMarker(slot);
        if (lockFile is not null || HasBytes(slot, bytes))
        {
            return true;
        }

        if (!DeleteLeftoverSlot(key, slot, there, now, folders, historyFolders))
        {
            return false;
        }

        folders.ThrowIfLink();
        historyFolders.ThrowIfLink();
        lockFile = Files.CreateMarker(slot);
        return lockFile is not null;
    }

    /// <summary>
    /// Renames the temporary file of a write back into the name of the file that it deleted through its hold (M2.9b), without
    /// replacing, and tries again after each of <see cref="RenameWaits"/> while the name is taken or the rename is refused:
    /// a file system that does not delete through the handle at once keeps the name until the last reader of the file lets go
    /// (<see cref="IFileSystem.DeleteHeldFile"/>), and it is the one step where a failure leaves the document gone (code
    /// review of M2.9b). The held folders are checked before each try, as for a create (M2.3). <c>false</c> when it is still
    /// taken or refused.
    /// </summary>
    private bool MoveIntoFreedName(string from, string to, Holds folders)
    {
        for (var tries = 0; ; tries++)
        {
            try
            {
                folders.ThrowIfLink();
                if (Files.MoveFile(from, to, replace: false))
                {
                    return true;
                }
            }
            catch (Exception ex) when (IsHeldOpen(ex))
            {
                // Refused, as a name in the middle of a delete may be.
            }

            if (tries == RenameWaits.Count)
            {
                return false;
            }

            Files.Wait(RenameWaits[tries]);
        }
    }

    /// <summary>Whether a file, opened as itself, is empty, or has gone; a slot that a listing gave with no size is read so.</summary>
    private bool IsDeadLock(string path)
    {
        using var stream = Files.OpenRead(path);
        return stream is null || (DocumentFormat.TryReadBytes(stream, out var bytes) && bytes.Length == 0);
    }

    /// <summary>The folders below the root of a document in its folder: <c>records</c>, or none for a document at the root.</summary>
    private string[] FoldersOf(DocumentKey key) => Known(key).Folder is { } folder ? [folder] : [];

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
    /// is read through the held folders, opened as itself. Two do not count (M2.9b): an empty file, the lock of a write back
    /// that a crash of the machine of the files left, which the next write of its version takes over once it is old
    /// (<see cref="DeleteLeftoverSlot"/>), told by its length through a handle, as a listing may give a size that is not
    /// current (security review); and, over a good document there at the version written back, the slot of that version with
    /// the bytes of the file there (<paramref name="there"/>), a write of it under way or what one left, not a version that
    /// the copy did not see, which the lock decides (<see cref="TakeLock"/>).
    /// </summary>
    private bool IsBehind(DocumentKey key, int version, byte[] bytes, int maxEntries, byte[]? there = null)
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
            && (entry.IsLink || entry.IsFolder || entry.Size > 0 || !IsDeadLock(HistoryPath(key, slot)))
            && (slot > version || entry.IsLink || entry.IsFolder
                || (!HasBytes(HistoryPath(key, version), bytes) && (there is null || !HasBytes(HistoryPath(key, version), there)))));

        // An empty folder made a mount point in place would have given the listing of another folder, and its slots.
        historyFolders.ThrowIfLink();
        return behind;
    }
}

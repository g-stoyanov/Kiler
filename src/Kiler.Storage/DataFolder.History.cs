namespace Kiler.Storage;

/// <summary>
/// The trash and the history of documents (M2.5, design 5.6). A delete is a write without a next version: it names the
/// version it deletes, puts it into its history slot, which is the lock as for a write (M2.4), and moves the document to
/// <c>trash\</c>. A restore moves it back. An old version comes back as a new write of its content: the upper layer reads it
/// (<see cref="ReadVersion"/>), sets who changed it (M5), and writes it over the current version (<see cref="ReplaceDocument"/>).
/// </summary>
public sealed partial class DataFolder
{
    /// <summary>
    /// How many of the newest history slots <see cref="Versions"/> opens for their owners (decided in M2.6): about a second
    /// over SMB, estimated from the reads of M1.0b (about 12 ms a file), not measured. Anyone can put any number of slot names
    /// in a history folder, and a long history is slow to open whole.
    /// </summary>
    internal const int SlotsWithOwners = 100;

    /// <summary>
    /// Deletes the version <paramref name="version"/> of a document: the version into its history slot, as a write puts it
    /// there, then a move to the trash, over an older copy there, whose version is in history. Nothing is deleted when the
    /// document is missing, damaged or at another version, or when its slot is in the way (<see cref="WriteOutcome.Conflict"/>).
    /// The document keeps its times in the trash, as a restore does when it moves it back.
    /// </summary>
    internal WriteResult DeleteDocument(DocumentKey key, int version)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        using var folders = HoldDocumentFolders(key);
        if (!TryReadVersion(key, version, folders, out var old, out var onDisk, out var refused))
        {
            return refused;
        }

        NewFile? slotFile = null;
        var mayHaveMoved = false;
        try
        {
            using var historyFolders = HoldHistoryFolders(key);
            using var trashFolders = Hold(create: true, TrashFolders(key));

            // Before the slot, so a marker that cannot be made leaves no slot in the way of the next write or delete.
            using var sourceMarker = HoldMarker(DocumentPath(key));
            using var destinationMarker = HoldMarker(TrashPath(key));

            // The write time of the new temporary file of the slot is the time of the delete on the clock of the file server,
            // against which a leftover slot is old (M2.4).
            var slot = HistoryPath(key, version);
            slotFile = WriteTemporaryWithTimes(Path.GetDirectoryName(slot)!, Path.GetFileNameWithoutExtension(slot), old, out var written);
            if (!MakeSlot(key, slot, slotFile.Path, old, written.LastWriteTime, folders, historyFolders))
            {
                Discard(slotFile);
                return WriteResult.Conflict(onDisk);
            }

            // The slot stays open with write access until the document is in the trash, as for a write (M2.4). The times are
            // held only for the move: as any open handle, theirs stops a rename over the file (measured in M2.5), and from the
            // slot on no other write of Kiler renames over the document.
            using (var times = Files.OpenTimes(DocumentPath(key)))
            {
                if (times is null)
                {
                    DeleteOwnSlot(slotFile);
                    return WriteResult.Not(WriteOutcome.Missing);
                }

                MoveOutcome moved;
                try
                {
                    moved = MoveDocument(times, DocumentPath(key), TrashPath(key), replace: true, ref mayHaveMoved, folders, trashFolders);
                }
                catch (FileNotFoundException)
                {
                    // Someone took the document away during the waits.
                    DeleteOwnSlot(slotFile);
                    return WriteResult.Not(WriteOutcome.Missing);
                }

                if (moved != MoveOutcome.Moved)
                {
                    DeleteOwnSlot(slotFile);
                    return WriteResult.Not(WriteOutcome.Busy);
                }

                Files.KeepCreationTime(times);
            }

            return WriteResult.Deleted(version, old);
        }
        catch
        {
            // When the move may have been done and its answer lost, the slot stays, as for a write: it is the right history,
            // and when not, a write or a delete takes it over once it is old. When it was certainly not done, as when a step
            // before it failed, the document is as it was, and the slot goes through its own handle, as for a busy delete, so
            // the next one does not meet it (fourth security review of M2.5).
            if (slotFile is not null)
            {
                if (!mayHaveMoved)
                {
                    DeleteOwnSlot(slotFile);
                }

                Discard(slotFile);
            }

            throw;
        }
        finally
        {
            slotFile?.Dispose();
        }
    }

    /// <summary>
    /// Restores a document from the trash: a move back that does not replace, so a document with its id that is there gives
    /// <see cref="WriteOutcome.Exists"/>. It comes back at its version, which its delete put into its history slot, so that
    /// slot goes, as a takeover deletes one but at any age: else the next write or delete of that version would meet it and
    /// wait for two minutes (decided in M2.5). A slot that the restore cannot hold or delete, or with other bytes, or one that
    /// a write has taken over since the document came back, stays; the restore is done all the same. A copy in the trash that
    /// is damaged is not restored, and when another restore was first, the copy is <see cref="WriteOutcome.Missing"/>.
    /// </summary>
    internal WriteResult RestoreFromTrash(DocumentKey key)
    {
        using var folders = HoldDocumentFolders(key);
        using var trashFolders = HoldIfThere(TrashFolders(key));
        if (trashFolders is null)
        {
            return WriteResult.Not(WriteOutcome.Missing);
        }

        var inTrash = TrashPath(key);
        byte[] bytes;
        using (var stream = Files.OpenRead(inTrash))
        {
            if (stream is null)
            {
                return WriteResult.Not(WriteOutcome.Missing);
            }

            if (!DocumentFormat.TryReadBytes(stream, out bytes))
            {
                return WriteResult.Damaged(DocumentDamage.TooLarge);
            }
        }

        // An empty folder made a mount point in place would have given the file of that name elsewhere.
        trashFolders.ThrowIfLink();
        var read = DocumentFormat.Read(key, bytes);
        if (read.IsDamaged)
        {
            return WriteResult.Damaged(read.Damage.Value);
        }

        // The times are held only for the move: as any open handle, theirs stops a rename over the file (measured in M2.5).
        FileStamp stamp;
        using (var sourceMarker = HoldMarker(inTrash))
        using (var destinationMarker = HoldMarker(DocumentPath(key)))
        using (var times = Files.OpenTimes(inTrash))
        {
            if (times is null)
            {
                return WriteResult.Not(WriteOutcome.Missing);
            }

            MoveOutcome moved;
            try
            {
                var mayHaveMoved = false;
                moved = MoveDocument(times, inTrash, DocumentPath(key), replace: false, ref mayHaveMoved, trashFolders, folders);
            }
            catch (FileNotFoundException)
            {
                // Another restore of the document moved it first.
                return WriteResult.Not(WriteOutcome.Missing);
            }

            if (moved != MoveOutcome.Moved)
            {
                return WriteResult.Not(moved == MoveOutcome.NameTaken ? WriteOutcome.Exists : WriteOutcome.Busy);
            }

            Files.KeepCreationTime(times);
            stamp = times.Stamp;
        }

        var version = read.Document.Version;
        try
        {
            using var historyFolders = HoldIfThere(HistoryFolders(key));
            if (historyFolders is not null)
            {
                DeleteLeftoverSlot(key, HistoryPath(key, version), bytes, now: null, folders, historyFolders);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The document is back. The slot stays, and a write takes it over once it is old; a link in the way of its delete
            // is not followed.
        }

        return WriteResult.Written(version, stamp, bytes);
    }

    /// <summary>
    /// The versions of a document, in order: its history slots, from one listing of its history folder, and its current
    /// version, when it is there and not damaged, last. Only the names that the layer gives its slots count; a link, a
    /// folder, any other name, and a slot whose times are after the year 9999 (M2.7) are skipped. The temporary files of the slots are skipped too, and those older than
    /// <see cref="TemporaryFileAge"/> at <paramref name="now"/> deleted, as a crash leaves them (M2.3). A deleted document has
    /// only its slots. The current version is read first, so a write in between gives a slot with the number of the current
    /// version, or after it, never a gap; a slot of a write under way, or a leftover, can have that number too. What the times
    /// and the owner of a slot say is in <see cref="DocumentVersion"/>. The owner of the current version is read through the
    /// handle it is read with, and each of the newest <see cref="SlotsWithOwners"/> slots is opened as itself for its owner,
    /// one open per slot (decided in M2.6); the older ones have none, and <see cref="ReadVersion"/> gives the owner of any.
    /// When there is a current version, the newest are chosen up to the version after it, as a slot far above it is none
    /// that a write made, and names such as <c>v2000000000.json</c> would push the real ones out.
    /// The stamp of a slot comes from the listing and its owner from that open, so a slot replaced in between gives the two
    /// of two files.
    /// </summary>
    internal IReadOnlyList<DocumentVersion> Versions(DocumentKey key, DateTimeOffset now)
    {
        var versions = new List<DocumentVersion>();
        int? current = null;
        using var folders = HoldDocumentFolders(key);
        using (var stream = Files.OpenRead(DocumentPath(key)))
        {
            folders.ThrowIfLink();
            if (stream is not null)
            {
                var stamp = Files.Stamp(stream);
                var read = DocumentFormat.Read(key, stream);
                if (!read.IsDamaged)
                {
                    current = read.Document.Version;
                    versions.Add(new DocumentVersion(read.Document.Version, stamp, IsCurrent: true, Files.Owner(stream)));
                }
            }
        }

        using (var historyFolders = HoldIfThere(HistoryFolders(key)))
        {
            if (historyFolders is not null)
            {
                var path = HistoryFolderPath(key);
                var temporary = new List<FileEntry>();
                var slots = new List<(int Version, FileStamp Stamp)>();
                foreach (var entry in Files.List(path))
                {
                    // A slot whose times are after the year 9999 has no stamp to give (M2.7).
                    if (entry.IsLink || entry.IsFolder || entry.TimesUnread)
                    {
                        continue;
                    }

                    if (IsTemporaryName(entry.Name))
                    {
                        temporary.Add(entry);
                    }
                    else if (SlotVersion(entry.Name) is { } version)
                    {
                        slots.Add((version, entry.Stamp));
                    }
                }

                // Anyone can put names in the folder, and each owner is a request of its own.
                var owned = slots
                    .Select(slot => slot.Version)
                    .Where(version => current is not { } last || version <= (long)last + 1)
                    .OrderDescending()
                    .Take(SlotsWithOwners)
                    .ToHashSet();
                foreach (var (version, stamp) in slots)
                {
                    versions.Add(new DocumentVersion(version, stamp, IsCurrent: false, owned.Contains(version) ? SlotOwner(key, version) : null));
                }

                // An empty folder made a mount point in place would have given the listing of another folder, and its slots.
                historyFolders.ThrowIfLink();
                DeleteOldTemporaryFiles(path, temporary, now);
            }
        }

        return [.. versions.OrderBy(version => version.Version).ThenBy(version => version.IsCurrent)];
    }

    /// <summary>
    /// The owner of a history slot, opened as itself for it, through its held folders (M2.6). <c>null</c> when it cannot be
    /// read, also when the slot cannot be opened: someone holds it without sharing, it has gone since the listing, or a link
    /// has been put at its name. The slot stays in the list all the same.
    /// </summary>
    private string? SlotOwner(DocumentKey key, int version)
    {
        try
        {
            using var slot = Files.OpenRead(HistoryPath(key, version));
            return slot is null ? null : Files.Owner(slot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Holds a marker of the layer beside a file that a move to the trash or back takes away or brings, from before the
    /// checks of the slot and the move until the move and its checks are done (second and third security reviews of M2.5).
    /// The folder is then never empty, so no one can make it a mount point in place (<see cref="IFileSystem.CreateMarker"/>):
    /// the trash folder at its first delete, the document folder at the restore of its only document, or the folder that
    /// someone has emptied during the waits. A link made before the marker and still there at the check is refused by the
    /// check; one made and undone between them is not seen, as M2.3 says for the other steps, and the marker made through it
    /// is elsewhere (not confirmed through the handle of the held folder).
    /// </summary>
    private IDisposable HoldMarker(string beside)
    {
        var marker = Path.Combine(Path.GetDirectoryName(beside)!, TemporaryName(Path.GetFileNameWithoutExtension(beside)));

        // The random name is never taken, unless a folder of the path has been made a link.
        return Files.CreateMarker(marker) ?? throw new IOException($"The marker cannot be made, as its name is taken: {marker}");
    }

    /// <summary>
    /// Moves a document to the trash or back, as <see cref="TryMove"/> does with the folders of both names, while the caller
    /// holds the markers of both (<see cref="HoldMarker"/>). After the move, the file at the new name, opened as itself, must
    /// have the id of the file that <paramref name="times"/> holds: someone who renamed the document away during the waits
    /// and put a folder, a link or another file at its name got that moved, and the move is refused, never given as done.
    /// The check binds the move to the file held for its times, which is opened after the document was read: a file swapped
    /// in between is not seen, as anyone on the share can change the document anyway.
    /// </summary>
    private MoveOutcome MoveDocument(FileTimes times, string from, string to, bool replace, ref bool mayHaveMoved, Holds sourceFolders, Holds destinationFolders)
    {
        var moved = TryMove(from, to, replace, ref mayHaveMoved, sourceFolders, destinationFolders);
        if (moved == MoveOutcome.Moved)
        {
            ThrowIfNotMoved(times, to);
        }

        return moved;
    }

    /// <summary>An <see cref="IOException"/> unless the file at <paramref name="to"/>, opened as itself, is the file of <paramref name="times"/>.</summary>
    private void ThrowIfNotMoved(FileTimes times, string to)
    {
        FileTimes? moved;
        try
        {
            moved = Files.OpenTimes(to);
        }
        catch (UnauthorizedAccessException)
        {
            // A folder at the new name; a link is refused as an IOException.
            moved = null;
        }

        using (moved)
        {
            if (moved?.Id != times.Id)
            {
                throw new IOException($"Another file than the one held for its times was moved: {to}");
            }
        }
    }

    /// <summary>
    /// Reads an old version of a document from its history slot: <c>null</c> when there is no slot of that version, else the
    /// document as it was, or why the slot is damaged, with the owner of the slot, read through the same handle (M2.6), which
    /// is who wrote the version after it or deleted it (<see cref="DocumentVersion"/>). A slot with another version inside
    /// than its name is damaged (<see cref="DocumentDamage.WrongVersion"/>). The folders are held and the slot is opened as
    /// itself, so a link is an <see cref="IOException"/>, as for <see cref="Read"/>.
    /// </summary>
    internal DocumentRead? ReadVersion(DocumentKey key, int version)
    {
        var slot = HistoryPath(key, version);
        using var historyFolders = HoldIfThere(HistoryFolders(key));
        if (historyFolders is null)
        {
            return null;
        }

        using var stream = Files.OpenRead(slot);
        historyFolders.ThrowIfLink();
        if (stream is null)
        {
            return null;
        }

        var read = DocumentFormat.Read(key, stream);
        return (read.IsDamaged || read.Document.Version == version ? read : DocumentRead.Damaged(DocumentDamage.WrongVersion)).WithOwner(Files.Owner(stream));
    }
}

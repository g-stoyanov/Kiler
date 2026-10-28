using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Kiler.Storage;

/// <summary>
/// The writes of documents (M2.3, design 5.4). A write never writes into a file that is there: its bytes go into a new
/// temporary file, which a rename puts in place, so no file is ever half written under the name of a document or of a
/// history slot. The folders of the path are held from the root down while it works, and checked through their handles
/// before the renames. Once started, a write runs to its end, as the stop aborts requests after 5 s (M1.6), so it takes no
/// cancellation.
/// </summary>
public sealed partial class DataFolder
{
    /// <summary>
    /// The age from which a temporary file is a leftover of a crash. Its write time comes from the clock of the file server
    /// and is compared with the clock of this machine, so the limit allows for a difference of some minutes between them.
    /// </summary>
    internal static readonly TimeSpan TemporaryFileAge = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The age from which a history slot with the bytes of the document is a leftover of a crash, which the next write takes
    /// over (M2.4). Younger, it is another write under way: the SMB client waits for a request about 60 s before it gives
    /// up, so a write can stop for about a minute between its slot and its rename. Both times come from the file server.
    /// </summary>
    internal static readonly TimeSpan SlotAge = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The waits between the tries of a rename over a document that someone holds open (M2.2), such as another Kiler or an
    /// antivirus: about 1.6 s in all, well within the 5 s that the stop gives a request (M1.6).
    /// </summary>
    internal static readonly IReadOnlyList<TimeSpan> RenameWaits =
    [
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(800),
    ];

    /// <summary>The hex digits of the random part of a temporary name.</summary>
    private const int TemporaryDigits = 8;

    /// <summary>ERROR_SHARING_VIOLATION, which a rename over a file that someone holds open may give instead of access denied.</summary>
    private const int SharingViolation = unchecked((int)0x80070020);

    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    /// <summary>
    /// Creates a document: version 1 in a temporary file, then a rename that does not replace.
    /// <see cref="WriteOutcome.Exists"/> when the id is taken, a link too, or has a history. Content that would make a damaged
    /// file is an <see cref="ArgumentException"/>, before any file is made.
    /// </summary>
    internal WriteResult CreateDocument(DocumentKey key, JsonObject content)
    {
        var bytes = DocumentFormat.New(key, content);
        using var folders = HoldDocumentFolders(key);

        // A deleted document keeps its history (M2.5). A new version 1 would meet the old slot of version 1, which has other
        // bytes and is never taken over, so it could never be written. The id comes back by a restore from the trash.
        if (Files.Attributes(HistoryFolderPath(key)) is not null)
        {
            return WriteResult.Not(WriteOutcome.Exists);
        }

        using var file = WriteTemporaryWithTimes(Path.Combine(Root, key.Folder ?? ""), key.Id, bytes, out _);
        try
        {
            folders.ThrowIfLink();
            if (!Files.MoveFile(file.Path, DocumentPath(key), replace: false))
            {
                Discard(file);
                return WriteResult.Not(WriteOutcome.Exists);
            }

            return WriteResult.Written(1, Files.SetCreationToWriteTime(file), bytes);
        }
        catch
        {
            Discard(file);
            throw;
        }
    }

    /// <summary>
    /// Replaces the version <paramref name="version"/> of a document with the next one: a temporary file, the old version
    /// into its history slot, then a rename over the document. The slot is written whole under a temporary name and renamed
    /// to its own without replacing, which is atomic also over SMB, so it is made only when it is not there, and of two
    /// writes of one version the second stops there with a conflict (M2.4). A slot that a crash left is taken over
    /// (<see cref="MakeSlot"/>). Nothing is written when the document is missing, damaged, at another version or at the last
    /// one; content that would make a damaged file is an <see cref="ArgumentException"/>, before any file is made.
    /// </summary>
    internal WriteResult ReplaceDocument(DocumentKey key, int version, JsonObject content)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        using var folders = HoldDocumentFolders(key);
        if (!TryReadVersion(key, version, folders, out var old, out var onDisk, out var refused))
        {
            return refused;
        }

        if (version == int.MaxValue)
        {
            return WriteResult.Not(WriteOutcome.NoNextVersion);
        }

        var bytes = DocumentFormat.Next(key, version, content);
        using var file = WriteTemporaryWithTimes(Path.Combine(Root, key.Folder ?? ""), key.Id, bytes, out var written);
        NewFile? slotFile = null;
        try
        {
            using var historyFolders = HoldHistoryFolders(key);
            var slot = HistoryPath(key, version);
            slotFile = WriteTemporary(Path.GetDirectoryName(slot)!, Path.GetFileNameWithoutExtension(slot), old);
            if (!MakeSlot(key, slot, slotFile.Path, old, written.LastWriteTime, folders, historyFolders))
            {
                Discard(slotFile);
                Discard(file);
                return WriteResult.Conflict(onDisk);
            }

            // The slot stays open with write access until the document is replaced, so no other write can hold it to take it
            // over while this one is alive, however long it stops (M2.4).
            // On any other failure the slot stays (below), as the rename may have been done, so this write needs no more than
            // the outcome.
            var mayHaveMoved = false;
            if (TryMove(file.Path, DocumentPath(key), replace: true, ref mayHaveMoved) != MoveOutcome.Moved)
            {
                // The document is as it was, so the slot goes too, and the write can be tried again at once.
                Discard(file);
                DeleteOwnSlot(slotFile);
                return WriteResult.Not(WriteOutcome.Busy);
            }

            return WriteResult.Written(version + 1, Files.SetCreationToWriteTime(file), bytes, old);
        }
        catch
        {
            // The slot stays: when the rename was done and its answer lost, it is the right history, and when not, a write
            // takes it over once it is old (M2.4).
            Discard(file);
            if (slotFile is not null)
            {
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
    /// Deletes the temporary files of a listing of <paramref name="folder"/> that are older than
    /// <see cref="TemporaryFileAge"/> at <paramref name="now"/>: a crash in the middle of a write leaves them. One that
    /// cannot be deleted now is left for the next listing. Gives how many went.
    /// </summary>
    internal int DeleteOldTemporaryFiles(string? folder, FolderListing listing, DateTimeOffset now)
    {
        var path = FolderPath(folder);
        using var held = folder is null ? Hold(create: false) : Hold(create: false, folder);
        return DeleteOldTemporaryFiles(path, listing.Temporary, now);
    }

    /// <summary>
    /// A name that is never a document's or an account's, as <see cref="StorageName"/> refuses the ending:
    /// <c>&lt;name&gt;.&lt;8 hex digits&gt;.tmp</c>. The writes name their temporary files so, <see cref="CreatePrivate(string)"/> its
    /// temporary folder, and the local copy its own temporary files (M2.7).
    /// </summary>
    internal static string TemporaryName(string name) =>
        $"{name}.{RandomNumberGenerator.GetHexString(TemporaryDigits, lowercase: true)}{StorageName.TemporaryEnding}";

    /// <summary>Whether a name is one that <see cref="TemporaryName"/> makes.</summary>
    internal static bool IsTemporaryName(string name)
    {
        var suffix = 1 + TemporaryDigits + StorageName.TemporaryEnding.Length;
        if (name.Length <= suffix || !name.EndsWith(StorageName.TemporaryEnding, StringComparison.Ordinal) || name[^suffix] != '.')
        {
            return false;
        }

        return !name.AsSpan(name.Length - suffix + 1, TemporaryDigits).ContainsAnyExcept(LowerHex) && StorageName.IsValid(name[..^suffix]);
    }

    /// <summary>The name that <see cref="TemporaryName"/> made a name of, or <c>null</c> for a name that it does not make (M2.7e).</summary>
    internal static string? TemporaryStem(string name) => IsTemporaryName(name) ? name[..^(1 + TemporaryDigits + StorageName.TemporaryEnding.Length)] : null;

    /// <summary>
    /// Whether a step was refused because someone holds the file open, or it is read-only: access denied or a sharing
    /// violation.
    /// </summary>
    private static bool IsHeldOpen(Exception ex) => ex is UnauthorizedAccessException || (ex is IOException && ex.HResult == SharingViolation);

    /// <summary>The old temporary files of a held folder, as the public overload deletes them.</summary>
    private int DeleteOldTemporaryFiles(string folder, IEnumerable<FileEntry> temporary, DateTimeOffset now)
    {
        var deleted = 0;
        foreach (var entry in temporary)
        {
            // A name from the shared folder becomes a path only when it is the name of a temporary file of the layer. One
            // whose time could not be read is never taken for old.
            if (!IsTemporaryName(entry.Name) || entry.TimesUnread || now - entry.LastWriteTime < TemporaryFileAge)
            {
                continue;
            }

            try
            {
                Files.DeleteFile(Path.Combine(folder, entry.Name));
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder at the name, or a file that someone holds without delete sharing: the next listing tries again.
            }
        }

        return deleted;
    }

    /// <summary>
    /// Reads the document that a write or a delete changes, through its held folders: its bytes as they are on disk, which go
    /// into the history slot unchanged, and the document. <c>false</c>, with the result to give, when nothing may be written:
    /// the document is missing, damaged or at another version than <paramref name="version"/>.
    /// </summary>
    private bool TryReadVersion(
        DocumentKey key, int version, Holds folders, out byte[] old, [NotNullWhen(true)] out Document? onDisk, [NotNullWhen(false)] out WriteResult? refused)
    {
        onDisk = null;
        refused = null;
        using (var stream = Files.OpenRead(DocumentPath(key)))
        {
            if (stream is null)
            {
                old = [];
                refused = WriteResult.Not(WriteOutcome.Missing);
                return false;
            }

            if (!DocumentFormat.TryReadBytes(stream, out old))
            {
                refused = WriteResult.Damaged(DocumentDamage.TooLarge);
                return false;
            }
        }

        // An empty folder made a mount point in place would have given the file of that name elsewhere.
        folders.ThrowIfLink();

        var read = DocumentFormat.Read(key, old);
        if (read.IsDamaged)
        {
            refused = WriteResult.Damaged(read.Damage.Value);
            return false;
        }

        if (read.Document.Version != version)
        {
            refused = WriteResult.Conflict(read.Document);
            return false;
        }

        onDisk = read.Document;
        return true;
    }

    /// <summary>Lets go of a temporary file that will not become the document or the slot, and deletes it, as far as it can.</summary>
    private void Discard(NewFile file)
    {
        file.Dispose();
        try
        {
            Files.DeleteFile(file.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An old temporary file is deleted later (DeleteOldTemporaryFiles).
        }
    }

    /// <summary>The bytes in a new temporary file <c>&lt;name&gt;.&lt;8 hex digits&gt;.tmp</c> in a held folder, held open.</summary>
    private NewFile WriteTemporary(string folder, string name, byte[] bytes)
    {
        var path = Path.Combine(folder, TemporaryName(name));
        // The random name is never taken, unless a folder of the path has been made a link (measured in M2.3).
        return Files.CreateFile(path, bytes) ?? throw new IOException($"The temporary file cannot be made, as its name is taken: {path}");
    }

    /// <summary>
    /// The bytes in a new temporary file, held open, with its creation time set to its write time at once, so a document has
    /// the right times also when the write stops after the rename and before they are set again. <paramref name="stamp"/>
    /// gives its size and times, from the clock of the file server.
    /// </summary>
    private NewFile WriteTemporaryWithTimes(string folder, string name, byte[] bytes, out FileStamp stamp)
    {
        var file = WriteTemporary(folder, name, bytes);
        try
        {
            stamp = Files.SetCreationToWriteTime(file);
            return file;
        }
        catch
        {
            Discard(file);
            throw;
        }
    }

    /// <summary>
    /// Renames the temporary file of a history slot to the slot, without replacing: the lock of the write (M2.4). When the
    /// slot is there, a leftover of a crash is deleted (<see cref="DeleteLeftoverSlot"/>) and the rename is tried once more,
    /// which decides between this write and any other that has come in between. <c>false</c>, a conflict, when the slot is
    /// another write under way, a slot that is not taken over, or the rename after a takeover finds it taken again.
    /// <paramref name="now"/> is the write time of the new temporary file of this write, on the clock of the file server.
    /// </summary>
    private bool MakeSlot(DocumentKey key, string slot, string temporary, byte[] old, DateTimeOffset now, Holds folders, Holds historyFolders)
    {
        folders.ThrowIfLink();
        historyFolders.ThrowIfLink();
        if (Files.MoveFile(temporary, slot, replace: false))
        {
            return true;
        }

        if (!DeleteLeftoverSlot(key, slot, old, now, folders, historyFolders))
        {
            return false;
        }

        folders.ThrowIfLink();
        historyFolders.ThrowIfLink();
        return Files.MoveFile(temporary, slot, replace: false);
    }

    /// <summary>
    /// Deletes a history slot that a crash left between the slot and the rename over the document: one with the bytes of the
    /// document, now as when the write read it, and <see cref="SlotAge"/> older than <paramref name="now"/> by its write
    /// time, both from the file server. A younger slot is another write under way; one with other bytes is not the version
    /// that the write replaces; and when the document has changed since the write read it, the slot is the history of the
    /// write that changed it, which a write that stopped for a while after its read finds old. None of them is ever taken
    /// over, nor a link, a folder or a slot that may not be deleted. The slot is held from its read to its delete, so of two
    /// writes that take over one slot the second cannot hold it, and a write under way, which keeps its slot open to write,
    /// cannot lose it. It is deleted through the handle, so the slot that was read goes, never a new one at its name, and
    /// its name is free once the hold is let go (<see cref="IFileSystem.DeleteHeldFile"/>). Without <paramref name="now"/>
    /// the age does not count: a restore from the trash deletes so the slot that the delete made (M2.5). <c>false</c> when
    /// nothing was deleted.
    /// </summary>
    private bool DeleteLeftoverSlot(DocumentKey key, string slot, byte[] old, DateTimeOffset? now, Holds folders, Holds historyFolders)
    {
        try
        {
            // A hold, which opens a file as itself, would throw on a link.
            if (Files.Attributes(slot) is not { } attributes || (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            {
                return false;
            }

            using var held = Files.HoldFile(slot);
            if (held is null || (now is { } time && time - held.LastWriteTime < SlotAge))
            {
                return false;
            }

            if (!DocumentFormat.TryReadBytes(held.Content, out var bytes) || !bytes.AsSpan().SequenceEqual(old) || !IsUnchanged(key, old))
            {
                return false;
            }

            // The reads went through the held folders, and the delete goes through them too.
            folders.ThrowIfLink();
            historyFolders.ThrowIfLink();
            Files.DeleteHeldFile(held);
            return true;
        }
        catch (Exception ex) when (IsHeldOpen(ex))
        {
            // Another write holds it, to take it over or as its own slot; someone else holds it or the document open without
            // sharing; or it is read-only.
            return false;
        }
    }

    /// <summary>Whether the document has the bytes that the write read at its start.</summary>
    private bool IsUnchanged(DocumentKey key, byte[] old) => HasBytes(DocumentPath(key), old);

    /// <summary>Whether a file, opened as itself, has the bytes; <c>false</c> when no file is there.</summary>
    private bool HasBytes(string path, byte[] expected)
    {
        using var stream = Files.OpenRead(path);
        return stream is not null && DocumentFormat.TryReadBytes(stream, out var bytes) && bytes.AsSpan().SequenceEqual(expected);
    }

    /// <summary>
    /// Deletes the slot of this write or delete after the move of the document was refused, through its own handle (M2.5):
    /// the slot shares the delete, so someone may have deleted it during the waits and another write made its own at its
    /// name, which a delete by the name would take. A slot that cannot be deleted stays as a leftover with the bytes of the
    /// document, and a write takes it over once it is old.
    /// </summary>
    private void DeleteOwnSlot(NewFile slot)
    {
        try
        {
            Files.DeleteNewFile(slot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It stays as a leftover.
        }
    }

    /// <summary>
    /// Moves a file, as <see cref="IFileSystem.MoveFile"/> does: a temporary file over the document, or a document to the
    /// trash and back (M2.5). Someone who holds the file at either name open refuses it (M2.2), so it is tried again after
    /// each of <see cref="RenameWaits"/>; <see cref="MoveOutcome.Refused"/> when it is still refused. The held folders of
    /// <paramref name="check"/> are checked before each try and after the move: a folder that is empty, such as the trash
    /// folder after a restore, can be made a mount point in place during a wait (security review of M2.5), and a move through
    /// one is never given as done. A folder made a link right after the check and made a folder again before the check after
    /// the move is not seen, as for the other steps (M2.3). <paramref name="mayHaveMoved"/> says, also when it throws, whether
    /// the file may have moved: a move that went through, or one that failed otherwise than as refused or as a missing file,
    /// whose answer may have been lost (fourth security review of M2.5).
    /// </summary>
    private MoveOutcome TryMove(string from, string to, bool replace, ref bool mayHaveMoved, params ReadOnlySpan<Holds> check)
    {
        for (var tries = 0; ; tries++)
        {
            try
            {
                ThrowIfLink(check);
                mayHaveMoved = true;
                if (!Files.MoveFile(from, to, replace))
                {
                    mayHaveMoved = false;
                    return MoveOutcome.NameTaken;
                }

                ThrowIfLink(check);
                return MoveOutcome.Moved;
            }
            catch (Exception ex) when (IsHeldOpen(ex) && tries < RenameWaits.Count)
            {
                mayHaveMoved = false;
                Files.Wait(RenameWaits[tries]);
            }
            catch (Exception ex) when (IsHeldOpen(ex))
            {
                mayHaveMoved = false;
                return MoveOutcome.Refused;
            }
            catch (FileNotFoundException)
            {
                mayHaveMoved = false;
                throw;
            }
        }
    }

    private static void ThrowIfLink(ReadOnlySpan<Holds> check)
    {
        foreach (var holds in check)
        {
            holds.ThrowIfLink();
        }
    }

    /// <summary>The root and the folder of a document, held.</summary>
    private Holds HoldDocumentFolders(DocumentKey key) => Known(key).Folder is { } folder ? Hold(create: false, folder) : Hold(create: false);

    /// <summary>The folders of the history slots of a document, from the root down, made where they are missing, and held.</summary>
    private Holds HoldHistoryFolders(DocumentKey key) => Hold(create: true, HistoryFolders(key));

    /// <summary>The folders below the root of the history slots of a document: <c>history\records\8f3a1c</c>, <c>history\settings</c>.</summary>
    private string[] HistoryFolders(DocumentKey key) => Known(key).Folder is { } folder ? [History, folder, key.Id] : [History, key.Id];

    /// <summary>The folders below the root of the copy of a document in the trash: <c>trash\records</c>, or <c>trash</c> for a document at the root.</summary>
    private string[] TrashFolders(DocumentKey key) => Known(key).Folder is { } folder ? [Trash, folder] : [Trash];

    /// <summary>
    /// Holds the root and then the folders below it as <see cref="Hold"/> does, without making any, or gives <c>null</c> when
    /// one below the root is missing: <c>history\&lt;folder&gt;\</c> and <c>trash\&lt;folder&gt;\</c> come only with the first
    /// write or delete that needs them (M2.1). A missing root is a <see cref="DirectoryNotFoundException"/>.
    /// </summary>
    private Holds? HoldIfThere(params ReadOnlySpan<string> folders)
    {
        var holds = new Holds();
        try
        {
            var path = HoldRoot(holds);
            foreach (var folder in folders)
            {
                path = Path.Combine(path, folder);
                try
                {
                    HoldBelow(holds, path, create: false);
                }
                catch (DirectoryNotFoundException)
                {
                    holds.Dispose();
                    return null;
                }
            }

            return holds;
        }
        catch
        {
            holds.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Holds the root and then each folder below it on the way down, each opened as itself through the folders held above
    /// it, so none of them can be renamed or deleted until they are let go. With <paramref name="create"/>, a missing folder
    /// below the root is made first, one at a time, in the same call that holds it (M2.5). In a private folder, each must
    /// have the owner of the root (<see cref="HoldBelow"/>, M2.7d).
    /// </summary>
    private Holds Hold(bool create, params ReadOnlySpan<string> folders)
    {
        var holds = new Holds();
        try
        {
            var path = HoldRoot(holds);
            foreach (var folder in folders)
            {
                path = Path.Combine(path, folder);
                HoldBelow(holds, path, create);
            }

            return holds;
        }
        catch
        {
            holds.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Holds the root, the first of <paramref name="holds"/>, and gives its path. A private folder is held from the data
    /// folder down, through <c>private\</c>, each opened as itself, as <see cref="CreatePrivate(string)"/> holds them: opened by its
    /// path alone, its root would be reached through a <c>private\</c> that anyone could swap for a link to another folder
    /// between the check of <see cref="OpenPrivate(string)"/> and the hold (security review of M2.7b). Its root must still
    /// be the folder that it was opened as, by its owner and its id (<see cref="Identity"/>, M2.7d): else a
    /// <see cref="ForeignFolderException"/>, before anything is read or written there.
    /// </summary>
    private string HoldRoot(Holds holds)
    {
        if (Account is null)
        {
            holds.Add(Files.HoldFolder(Root));
        }
        else if (HoldPrivateRoot(Files, holds, Root) != Identity)
        {
            throw new ForeignFolderException($"The private folder is not the one Kiler knows: another folder, or another owner: {Root}");
        }

        return Root;
    }

    /// <summary>
    /// Holds a folder below the root and adds it to <paramref name="holds"/>. With <paramref name="create"/>, a folder that is
    /// there is opened as it was before M2.5, and only a missing one goes through the create, which on a file server may ask
    /// for the right to make a folder in the one above even when the folder is there (not measured, M2.11); the create also
    /// holds a folder that someone made in between. Without it, a missing folder is a <see cref="DirectoryNotFoundException"/>.
    /// In a private folder, the folder must have the owner of the root (<see cref="ThrowIfForeign"/>, M2.7d).
    /// </summary>
    private void HoldBelow(Holds holds, string path, bool create)
    {
        var readOwner = Account is not null;
        PinnedFolder held;
        try
        {
            held = Files.HoldFolder(path, readOwner);
        }
        catch (DirectoryNotFoundException) when (create)
        {
            held = Files.HoldOrCreateFolder(path, readOwner);
        }

        holds.Add(held);
        ThrowIfForeign(held);
    }

    /// <summary>
    /// Holds the root of a private folder from the data folder down, through <c>private\</c>, each opened as itself, adds
    /// them to <paramref name="holds"/>, and gives what the root is: its owner and its id, read through its handle (M2.7d).
    /// An owner that cannot be read, or an id that tells nothing (index 0), is an <see cref="IOException"/>: the folder cannot
    /// be checked.
    /// </summary>
    private static FolderIdentity HoldPrivateRoot(IFileSystem files, Holds holds, string root)
    {
        var privateFolders = Path.GetDirectoryName(root)!;
        holds.Add(files.HoldFolder(Path.GetDirectoryName(privateFolders)!));
        holds.Add(files.HoldFolder(privateFolders));
        var held = files.HoldFolder(root, readOwner: true);
        holds.Add(held);
        var owner = files.Owner(held) ?? throw new IOException($"Kiler cannot read the owner of the private folder, which it checks: {root}");
        var id = files.Id(held);
        return id.Index != 0 ? new FolderIdentity(owner, id) : throw new IOException($"The file system gives the private folder no id, which Kiler checks: {root}");
    }

    /// <summary>
    /// In a private folder, a <see cref="ForeignFolderException"/> when a folder held below its root has another owner than
    /// the root (M2.7d): <c>CreatePrivate</c> makes them new, but someone could rename one away and put their own in its place
    /// (third security review of M2.6), and the history and the trash get their folders from the first write that needs them,
    /// by the user. An owner that cannot be read is an <see cref="IOException"/>. Nothing in the data folder. Accepting the
    /// private folder does not clear such a folder, as it is compared with the owner of the root, whatever root is accepted:
    /// it is put right on the share by its owner or by IT: a folder of the history or the trash is deleted, and the next write
    /// that needs it makes it again; <c>records\</c>, which only the create makes, is put back as a folder of the user
    /// (decided by the user after the review of M2.7d).
    /// </summary>
    private void ThrowIfForeign(PinnedFolder held)
    {
        if (Account is null)
        {
            return;
        }

        var owner = Files.Owner(held) ?? throw new IOException($"Kiler cannot read the owner of a folder of the private folder, which it checks: {held.HeldPath}");
        if (owner != Identity?.Owner)
        {
            throw new ForeignFolderException($"A folder of the private folder has another owner than the private folder: {held.HeldPath}");
        }
    }

    /// <summary>How <see cref="TryMove"/> ended.</summary>
    private enum MoveOutcome
    {
        Moved,

        /// <summary>Without replace: the new name is taken.</summary>
        NameTaken,

        /// <summary>Someone held a file at either name open for all the tries.</summary>
        Refused,
    }

    /// <summary>Folders held together, let go from the bottom up.</summary>
    internal sealed class Holds : IDisposable
    {
        private readonly Stack<PinnedFolder> _held = new();

        public void Add(PinnedFolder held) => _held.Push(held);

        /// <summary>
        /// An <see cref="IOException"/> when a held folder has become a link: the hold stops renames and deletes, but anyone
        /// who can write an empty folder can make it a mount point in place (measured in M2.3). On the developer machine the
        /// create-new of the temporary file through such a folder failed already; the check, after a read and before the
        /// renames, does not rely on that. A folder made a link and back between the steps is not seen.
        /// </summary>
        public void ThrowIfLink()
        {
            foreach (var held in _held)
            {
                held.ThrowIfLink();
            }
        }

        public void Dispose()
        {
            while (_held.TryPop(out var held))
            {
                held.Dispose();
            }
        }
    }
}

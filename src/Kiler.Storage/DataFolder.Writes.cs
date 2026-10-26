using System.Buffers;
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
    /// <see cref="WriteOutcome.Exists"/> when the id is taken, a link too. Content that would make a damaged file is an
    /// <see cref="ArgumentException"/>, before any file is made.
    /// </summary>
    internal WriteResult CreateDocument(DocumentKey key, JsonObject content)
    {
        var bytes = DocumentFormat.New(key, content);
        using var folders = HoldDocumentFolders(key);
        using var file = WriteTemporary(Path.Combine(Root, key.Folder ?? ""), key.Id, bytes, setTimes: true);
        try
        {
            folders.ThrowIfLink();
            if (!Files.MoveFile(file.Path, DocumentPath(key), replace: false))
            {
                Discard(file);
                return WriteResult.Not(WriteOutcome.Exists);
            }

            return WriteResult.Written(1, Files.SetCreationToWriteTime(file));
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
    /// to its own without replacing, which is atomic also over SMB, so it is made only when it is not there (M2.4), and of
    /// two writes of one version the second stops there. Nothing is written when the document is missing, damaged, at
    /// another version or at the last one; content that would make a damaged file is an <see cref="ArgumentException"/>,
    /// before any file is made.
    /// </summary>
    internal WriteResult ReplaceDocument(DocumentKey key, int version, JsonObject content)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        using var folders = HoldDocumentFolders(key);

        // The old version as it is on disk: its bytes go into the slot, unchanged.
        byte[] old;
        using (var stream = Files.OpenRead(DocumentPath(key)))
        {
            if (stream is null)
            {
                return WriteResult.Not(WriteOutcome.Missing);
            }

            if (!DocumentFormat.TryReadBytes(stream, out old))
            {
                return WriteResult.Damaged(DocumentDamage.TooLarge);
            }
        }

        // An empty folder made a mount point in place would have given the file of that name elsewhere.
        folders.ThrowIfLink();

        var onDisk = DocumentFormat.Read(key, old);
        if (onDisk.IsDamaged)
        {
            return WriteResult.Damaged(onDisk.Damage.Value);
        }

        if (onDisk.Document.Version != version)
        {
            return WriteResult.Conflict(onDisk.Document);
        }

        if (version == int.MaxValue)
        {
            return WriteResult.Not(WriteOutcome.NoNextVersion);
        }

        var bytes = DocumentFormat.Next(key, version, content);
        using var file = WriteTemporary(Path.Combine(Root, key.Folder ?? ""), key.Id, bytes, setTimes: true);
        NewFile? slotFile = null;
        try
        {
            using var historyFolders = HoldHistoryFolders(key);
            var slot = HistoryPath(key, version);
            slotFile = WriteTemporary(Path.GetDirectoryName(slot)!, Path.GetFileNameWithoutExtension(slot), old, setTimes: false);
            folders.ThrowIfLink();
            historyFolders.ThrowIfLink();
            if (!Files.MoveFile(slotFile.Path, slot, replace: false))
            {
                Discard(slotFile);
                Discard(file);
                return WriteResult.Conflict(onDisk.Document);
            }

            slotFile.Dispose();
            if (!RenameOver(file.Path, DocumentPath(key)))
            {
                // The document is as it was, so the slot goes too, and the write can be tried again at once. The slot was
                // not held during the waits, so someone may have deleted it and made its emptied folder a link.
                Discard(file);
                historyFolders.ThrowIfLink();
                Files.DeleteFile(slot);
                return WriteResult.Not(WriteOutcome.Busy);
            }

            return WriteResult.Written(version + 1, Files.SetCreationToWriteTime(file));
        }
        catch
        {
            // The slot stays: when the rename was done and its answer lost, it is the right history (M2.4).
            Discard(file);
            if (slotFile is not null)
            {
                Discard(slotFile);
            }

            throw;
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
        var deleted = 0;
        foreach (var entry in listing.Temporary)
        {
            // A name from the shared folder becomes a path only when it is the name of a temporary file of the layer.
            if (!IsTemporaryName(entry.Name) || now - entry.LastWriteTime < TemporaryFileAge)
            {
                continue;
            }

            try
            {
                Files.DeleteFile(Path.Combine(path, entry.Name));
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
    /// A name that is never a document's or an account's, as <see cref="StorageName"/> refuses the ending:
    /// <c>&lt;name&gt;.&lt;8 hex digits&gt;.tmp</c>. The writes name their temporary files so, and
    /// <see cref="CreatePrivate"/> its temporary folder.
    /// </summary>
    private static string TemporaryName(string name) =>
        $"{name}.{RandomNumberGenerator.GetHexString(TemporaryDigits, lowercase: true)}{StorageName.TemporaryEnding}";

    /// <summary>Whether a name is one that <see cref="TemporaryName"/> makes.</summary>
    private static bool IsTemporaryName(string name)
    {
        var suffix = 1 + TemporaryDigits + StorageName.TemporaryEnding.Length;
        if (name.Length <= suffix || !name.EndsWith(StorageName.TemporaryEnding, StringComparison.Ordinal) || name[^suffix] != '.')
        {
            return false;
        }

        return !name.AsSpan(name.Length - suffix + 1, TemporaryDigits).ContainsAnyExcept(LowerHex) && StorageName.IsValid(name[..^suffix]);
    }

    /// <summary>Whether a rename failed because someone holds the file at the new name open, or it is read-only.</summary>
    private static bool IsHeldOpen(Exception ex) => ex is UnauthorizedAccessException || (ex is IOException && ex.HResult == SharingViolation);

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

    /// <summary>
    /// The bytes in a new temporary file <c>&lt;name&gt;.&lt;8 hex digits&gt;.tmp</c> in a held folder, held open. With
    /// <paramref name="setTimes"/>, its creation time is set to its write time at once, so a document has the right times
    /// also when the write stops after the rename and before they are set again.
    /// </summary>
    private NewFile WriteTemporary(string folder, string name, byte[] bytes, bool setTimes)
    {
        var path = Path.Combine(folder, TemporaryName(name));
        // The random name is never taken, unless a folder of the path has been made a link (measured in M2.3).
        var file = Files.CreateFile(path, bytes) ?? throw new IOException($"The temporary file cannot be made, as its name is taken: {path}");
        try
        {
            if (setTimes)
            {
                Files.SetCreationToWriteTime(file);
            }

            return file;
        }
        catch
        {
            Discard(file);
            throw;
        }
    }

    /// <summary>
    /// Renames the temporary file over the document. Someone who holds the document open refuses the rename (M2.2), so it
    /// is tried again after each of <see cref="RenameWaits"/>; <c>false</c> when it is still refused.
    /// </summary>
    private bool RenameOver(string temporary, string path)
    {
        for (var tries = 0; ; tries++)
        {
            try
            {
                Files.MoveFile(temporary, path, replace: true);
                return true;
            }
            catch (Exception ex) when (IsHeldOpen(ex) && tries < RenameWaits.Count)
            {
                Files.Wait(RenameWaits[tries]);
            }
            catch (Exception ex) when (IsHeldOpen(ex))
            {
                return false;
            }
        }
    }

    /// <summary>The root and the folder of a document, held.</summary>
    private Holds HoldDocumentFolders(DocumentKey key) => Known(key).Folder is { } folder ? Hold(create: false, folder) : Hold(create: false);

    /// <summary>The folders of the history slots of a document, from the root down, made where they are missing, and held.</summary>
    private Holds HoldHistoryFolders(DocumentKey key) =>
        Known(key).Folder is { } folder ? Hold(create: true, History, folder, key.Id) : Hold(create: true, History, key.Id);

    /// <summary>
    /// Holds the root and then each folder below it on the way down, each opened as itself through the folders held above
    /// it, so none of them can be renamed or deleted until they are let go. With <paramref name="create"/>, a missing folder
    /// below the root is made first, one at a time.
    /// </summary>
    private Holds Hold(bool create, params ReadOnlySpan<string> folders)
    {
        var holds = new Holds();
        try
        {
            var path = Root;
            holds.Add(Files.HoldFolder(path));
            foreach (var folder in folders)
            {
                path = Path.Combine(path, folder);
                holds.Add(create ? HoldOrCreate(path) : Files.HoldFolder(path));
            }

            return holds;
        }
        catch
        {
            holds.Dispose();
            throw;
        }
    }

    private PinnedFolder HoldOrCreate(string path)
    {
        try
        {
            return Files.HoldFolder(path);
        }
        catch (DirectoryNotFoundException)
        {
            Files.CreateFolder(path);
            return Files.HoldFolder(path);
        }
    }

    /// <summary>Folders held together, let go from the bottom up.</summary>
    private sealed class Holds : IDisposable
    {
        private readonly Stack<PinnedFolder> _held = new();

        public void Add(PinnedFolder held) => _held.Push(held);

        /// <summary>
        /// An <see cref="IOException"/> when a held folder has become a link: the hold stops renames and deletes, but anyone
        /// who can write an empty folder can make it a mount point in place (measured in M2.3). On NTFS the create-new of
        /// the temporary file through such a folder fails already; the check, before the renames, covers file servers where
        /// that was not measured. A folder made a link and back between the steps is not seen.
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

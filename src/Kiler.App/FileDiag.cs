using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Kiler.Storage;

namespace Kiler.App;

/// <summary>
/// <c>Kiler.exe --diag</c>, the checks of design section 14 that a program can make: many small JSON files in a temporary
/// subfolder, as Kiler keeps its records. It measures the writing, the listing with names and dates and the reading, one by
/// one and several at a time, and checks the replacing by rename (design 5.4) and the owner of new files (design 7.8). It
/// deletes only the files it created, then the subfolder when it is empty. The logic comes from the probe of M1.0b.
/// </summary>
internal sealed class FileDiag
{
    public const int DefaultFileCount = 2000;

    /// <summary>How many files are read at a time. Each degree reads its own part of the files, so no pass reads a file that an earlier pass has read.</summary>
    private static readonly int[] ParallelReads = [1, 8, 16, 32];

    private readonly DiagReport _report;
    private readonly TimeProvider _time;
    private readonly int _fileCount;

    public FileDiag(DiagReport report, TimeProvider time, int fileCount = DefaultFileCount)
    {
        if (fileCount <= 0 || fileCount % ParallelReads.Length != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileCount), fileCount, $"The files are read in {ParallelReads.Length} equal parts.");
        }

        _report = report;
        _time = time;
        _fileCount = fileCount;
    }

    /// <summary>Runs the checks in a temporary subfolder of <paramref name="folder"/>, which must exist. The result is in the report.</summary>
    public async Task RunAsync(string folder, CancellationToken stop)
    {
        string root;
        try
        {
            root = Path.GetFullPath(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            _report.Check("Test folder", false, ex.Message);
            return;
        }

        _report.HidePath(root, "<test folder>");
        if (!Directory.Exists(root))
        {
            _report.Check("Test folder", false, $"it does not exist, so give one that does: Kiler.exe --diag <folder> ({root})");
            return;
        }

        _report.Line($"Test folder: {root} ({Location(root)})");

        var name = string.Create(
            CultureInfo.InvariantCulture, $"kiler-diag-{_time.GetLocalNow():yyyyMMdd-HHmmss}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}");
        var work = Path.Combine(root, name);
        var madeWork = false;
        PinnedFolder? pin = null;

        // Only the paths in this list are deleted at the end, never anything else in the folder.
        var created = new List<string>(_fileCount + 1);
        try
        {
            // CreateDirectory does not fail on a folder that exists, and a folder that exists is not ours.
            if (Directory.Exists(work))
            {
                throw new IOException($"The temporary subfolder exists already: {work}");
            }

            Directory.CreateDirectory(work);
            madeWork = true;

            // Held until the files are deleted, so no one can swap the subfolder for a link to another folder, where the
            // writes, the replace and the deletes would land. Not every file server keeps the folders above it (M2.11).
            pin = PinnedFolder.Open(work);
            _report.Line($"Temporary subfolder: {name}");

            var records = Write(work, created, stop);
            CheckOwner("Owner of the new subfolder", new DirectoryInfo(work));
            CheckOwner("Owner of a new file", new FileInfo(records[0]));
            Replace(records[0], created);
            CheckOwner("Owner of the replaced file", new FileInfo(records[0]));
            List(work, stop);
            await ReadAsync(records, stop);
        }
        catch (OperationCanceledException)
        {
            _report.Check("Run", false, "stopped");
        }
        // The point is to find out what fails on the shared folder, so the run reports instead of crashing, and cleans up.
        catch (Exception ex)
        {
            _report.Check("Run", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                if (madeWork)
                {
                    Cleanup(work, created, pin);
                }
            }
            finally
            {
                pin?.Dispose();
            }
        }

        if (CopyFiles.IsNetworkPath(root))
        {
            _report.Line("Note: files that this machine has written may still be in its SMB client cache, so the reads can look faster than a first read on another machine.");
        }
    }

    /// <summary>What kind of storage a path is on, without the path itself.</summary>
    public static string Location(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) && CopyFiles.IsNetworkPath(path))
        {
            return "network share (UNC path)";
        }

        DriveType? kind;
        try
        {
            kind = Path.GetPathRoot(path) is { Length: > 0 } root ? new DriveInfo(root).DriveType : null;
        }
        catch (ArgumentException)
        {
            kind = null;
        }

        return kind switch
        {
            DriveType.Network => "network drive (mapped letter)",
            DriveType.Fixed => "local disk",
            DriveType.Removable => "removable disk",
            DriveType other => other.ToString(),
            null => "unknown",
        };
    }

    /// <summary>
    /// Whether <paramref name="owner"/> is <paramref name="user"/>, and who it is otherwise. Only the accounts of Windows
    /// itself keep their name in the file of the report: every other name is registered with <paramref name="report"/> to
    /// be hidden there, whatever kind of account it is (domain, machine, Microsoft Entra, an identity of a NAS).
    /// <paramref name="processOwner"/> is the owner that the process token gives new files (<see cref="WindowsIdentity.Owner"/>).
    /// </summary>
    public static (bool Ok, string Detail) DescribeOwner(
        SecurityIdentifier owner, SecurityIdentifier? user, SecurityIdentifier? processOwner, DiagReport report)
    {
        if (owner == user)
        {
            return (true, "the current user");
        }

        // The name, or the SID when Windows cannot name it.
        var name = AccountName(owner);
        if (!IsWindowsAccount(owner))
        {
            report.HideName(name, "<another account>");
        }

        // A process that runs as administrator can give new files to the Administrators group, on a local disk. Otherwise
        // the owner comes from the storage itself, which is what the check is about.
        return owner == processOwner
            ? (false, $"{name}, not the current user. Kiler runs as administrator, which gives new files to that owner; run it as a normal user")
            : (false, $"{name}, not the current user");
    }

    private string[] Write(string work, List<string> created, CancellationToken stop)
    {
        var records = new string[_fileCount];
        var contents = new byte[_fileCount][];
        for (var i = 0; i < _fileCount; i++)
        {
            records[i] = Path.Combine(work, string.Create(CultureInfo.InvariantCulture, $"r{i:D4}.json"));
            contents[i] = SampleRecord(i, version: 1);
        }

        var started = _time.GetTimestamp();
        for (var i = 0; i < _fileCount; i++)
        {
            stop.ThrowIfCancellationRequested();
            using var stream = new FileStream(records[i], FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created.Add(records[i]);
            stream.Write(contents[i]);
        }

        _report.Timing("Write, one by one", _fileCount, _time.GetElapsedTime(started));
        _report.Line($"Average file size: {contents.Sum(content => (long)content.Length) / _fileCount} bytes");
        return records;
    }

    /// <summary>Design 5.4: the new content goes to a temporary file, which is renamed over the old one.</summary>
    private void Replace(string target, List<string> created)
    {
        var content = SampleRecord(0, version: 2);
        var temporary = target + ".tmp";
        var started = _time.GetTimestamp();
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            created.Add(temporary);
            stream.Write(content);
        }

        File.Move(temporary, target, overwrite: true);
        var elapsed = _time.GetElapsedTime(started);

        // After the move the name is free again, and whatever appears there later is not ours.
        created.Remove(temporary);

        var replaced = File.ReadAllBytes(target).AsSpan().SequenceEqual(content) && !File.Exists(temporary);
        _report.Check("Replace by rename over the old file", replaced, DiagReport.Ms(elapsed));
    }

    private void List(string work, CancellationToken stop)
    {
        for (var pass = 1; pass <= 3; pass++)
        {
            stop.ThrowIfCancellationRequested();
            var started = _time.GetTimestamp();
            var count = 0;
            foreach (var file in new DirectoryInfo(work).EnumerateFiles())
            {
                // The name and the date come with the listing itself, as the change polling of design 5.3 needs.
                _ = (file.Name, file.LastWriteTimeUtc);
                count++;
            }

            _report.Timing($"List names and dates, pass {pass}", count, _time.GetElapsedTime(started));
            if (count != _fileCount)
            {
                _report.Check("Listing", false, $"{count} files instead of {_fileCount}");
            }
        }
    }

    private async Task ReadAsync(string[] records, CancellationToken stop)
    {
        var part = records.Length / ParallelReads.Length;
        for (var i = 0; i < ParallelReads.Length; i++)
        {
            var degree = ParallelReads[i];
            var elapsed = await ReadAllAsync(records[(i * part)..((i + 1) * part)], degree, stop);
            _report.Timing(degree == 1 ? "Read one by one" : $"Read {degree} at a time", part, elapsed);
        }

        // The first part once more, to see whether a second read comes from a cache.
        var again = await ReadAllAsync(records[..part], 1, stop);
        _report.Timing("Read the first part again, one by one", part, again);
    }

    private async Task<TimeSpan> ReadAllAsync(string[] paths, int degree, CancellationToken stop)
    {
        var started = _time.GetTimestamp();
        var options = new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = stop };
        await Parallel.ForEachAsync(paths, options, async (path, token) => _ = await File.ReadAllBytesAsync(path, token));
        return _time.GetElapsedTime(started);
    }

    /// <summary>Design 7.8 relies on the shared folder recording the account that creates a file as its owner.</summary>
    private void CheckOwner(string what, FileSystemInfo item)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var (ok, detail) = DescribeOwner(FileOwner.Of(item), identity.User, identity.Owner, _report);
            _report.Check(what, ok, detail);
        }
        // Reported, so that the other checks still run.
        catch (Exception ex)
        {
            _report.Check(what, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Cleanup(string work, List<string> created, PinnedFolder? pin)
    {
        // The subfolder is held from the moment it was made (PinnedFolder). Still, when it was swapped for a link before
        // that, the names in it are not ours.
        try
        {
            if (File.GetAttributes(work).HasFlag(FileAttributes.ReparsePoint))
            {
                _report.Check("Temporary subfolder removed", false, $"it is a link now, so nothing was deleted: {work}");
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _report.Check("Temporary subfolder removed", false, $"it cannot be read, so nothing was deleted: {ex.Message}");
            return;
        }

        var started = _time.GetTimestamp();
        var failed = 0;
        foreach (var path in created)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

        _report.Timing("Delete the files", created.Count, _time.GetElapsedTime(started));
        if (failed > 0)
        {
            _report.Line($"{failed} files could not be deleted.");
        }

        // The subfolder cannot be deleted while it is held. A swap now only swaps what the delete below removes: a link,
        // or a folder that is not empty and stays.
        pin?.Dispose();

        // Not recursive: whatever else is in the subfolder stays.
        try
        {
            Directory.Delete(work, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _report.Line($"The temporary subfolder could not be deleted: {ex.Message}");
        }

        var removed = !Directory.Exists(work);
        _report.Check("Temporary subfolder removed", removed, removed ? null : $"delete it by hand: {work}");
    }

    /// <summary>
    /// The accounts of Windows itself, which name no person and no product: SYSTEM, LOCAL SERVICE, NETWORK SERVICE and the
    /// BUILTIN groups. Not the NT SERVICE accounts, which carry the name of a service, perhaps an internal one.
    /// </summary>
    private static bool IsWindowsAccount(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
        || sid.IsWellKnown(WellKnownSidType.LocalServiceSid)
        || sid.IsWellKnown(WellKnownSidType.NetworkServiceSid)
        || sid.Value.StartsWith("S-1-5-32-", StringComparison.Ordinal);

    private static string AccountName(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        // A SID that no one can name, or a domain that does not answer.
        catch (Exception ex) when (ex is IdentityNotMappedException or Win32Exception or UnauthorizedAccessException)
        {
            return sid.Value;
        }
    }

    /// <summary>A made-up record shaped like design 5.2, with random strings in place of the encrypted parts.</summary>
    private byte[] SampleRecord(int index, int version)
    {
        var level1 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(96));
        var level3 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var json = string.Create(CultureInfo.InvariantCulture, $$$"""
            {"id":"r{{{index:D4}}}","template":"db","version":{{{version}}},"modifiedBy":"diag.user","modifiedAt":"{{{_time.GetUtcNow():O}}}","public":{"system":"Demo","function":"Diag","env":"test","title":"Diag record {{{index}}}"},"protected":{"1":"{{{level1}}}","3":"{{{level3}}}"}}
            """);
        return Encoding.UTF8.GetBytes(json);
    }
}

using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Kiler.Probe;

/// <summary>
/// Many small JSON files in a temporary subfolder: writing, listing with names and dates, reading,
/// replacing by rename (design 5.4) and the owner of new files (design 7.8).
/// </summary>
internal static class DiagMode
{
    private const int FileCount = 2000;
    private static readonly int[] ParallelReads = [1, 8, 16, 32];

    public static async Task<int> RunAsync(string folder, TimeSpan startup)
    {
        var report = new Report("diag");
        report.Line($"Process start to Main: {ProbeInfo.Ms(startup)}");

        string root;
        try
        {
            root = Path.GetFullPath(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            report.Check("Test folder", false, ex.Message);
            report.Save();
            return 2;
        }

        report.Hide(root, "<test folder>");
        if (!Directory.Exists(root))
        {
            report.Check("Test folder", false, $"does not exist: {root}");
            report.Save();
            return 2;
        }

        report.Line($"Test folder: {root} ({ProbeInfo.Location(root)})");

        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler onCtrlC = (_, e) =>
        {
            // The first Ctrl+C stops the run and cleans up. A second one ends the process at once.
            e.Cancel = !stop.IsCancellationRequested;
            stop.Cancel();
        };
        Console.CancelKeyPress += onCtrlC;

        var work = Path.Combine(root, $"kiler-probe-{DateTime.Now:yyyyMMdd-HHmmss}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}");
        var madeWork = false;

        // Only the paths in this list are deleted at the end, never anything else in the folder.
        var created = new List<string>(FileCount);
        try
        {
            if (Directory.Exists(work))
            {
                throw new IOException($"The temporary subfolder already exists: {work}");
            }

            Directory.CreateDirectory(work);
            madeWork = true;
            report.Line($"Temporary subfolder: {Path.GetFileName(work)}");

            var records = Write(work, created, report, stop.Token);
            CheckOwner("Owner of the new subfolder", () => new DirectoryInfo(work).GetAccessControl(AccessControlSections.Owner), report);
            CheckOwner("Owner of a new file", () => new FileInfo(records[0]).GetAccessControl(AccessControlSections.Owner), report);
            Replace(records[0], created, report);
            ListFolder(work, report, stop.Token);
            await ReadAsync(records, report, stop.Token);
        }
        catch (OperationCanceledException)
        {
            report.Check("Run", false, "stopped with Ctrl+C");
        }
        catch (Exception ex)
        {
            // The probe is there to find out what fails on the share, so it reports instead of crashing.
            report.Check("Run", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (madeWork)
            {
                Cleanup(work, created, report);
            }

            Console.CancelKeyPress -= onCtrlC;
        }

        if (ProbeInfo.IsNetwork(root))
        {
            report.Line("Note: files written by this machine may still be in its SMB client cache, so the reads can look faster than a first read on another machine.");
        }

        report.Save();
        return report.Failed ? 1 : 0;
    }

    private static string[] Write(string work, List<string> created, Report report, CancellationToken stop)
    {
        var records = new string[FileCount];
        var contents = new byte[FileCount][];
        for (var i = 0; i < FileCount; i++)
        {
            records[i] = Path.Combine(work, $"r{i:D4}.json");
            contents[i] = SampleRecord(i, version: 1);
        }

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < FileCount; i++)
        {
            stop.ThrowIfCancellationRequested();
            using var stream = new FileStream(records[i], FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created.Add(records[i]);
            stream.Write(contents[i]);
        }

        report.Timing("Write, one by one", FileCount, watch.Elapsed);
        report.Line($"Average file size: {contents.Sum(content => (long)content.Length) / FileCount} bytes");
        return records;
    }

    /// <summary>Design 5.4: the new content goes to a temporary file, which is renamed over the old one.</summary>
    private static void Replace(string target, List<string> created, Report report)
    {
        var content = SampleRecord(0, version: 2);
        var temp = target + ".tmp";
        var watch = Stopwatch.StartNew();
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            created.Add(temp);
            stream.Write(content);
        }

        File.Move(temp, target, overwrite: true);
        var elapsed = watch.Elapsed;

        // After the move the name is free again, and whatever appears there later is not ours.
        created.Remove(temp);

        var replaced = File.ReadAllBytes(target).AsSpan().SequenceEqual(content) && !File.Exists(temp);
        report.Check("Replace by rename over the old file", replaced, ProbeInfo.Ms(elapsed));
        CheckOwner("Owner of the replaced file", () => new FileInfo(target).GetAccessControl(AccessControlSections.Owner), report);
    }

    private static void ListFolder(string work, Report report, CancellationToken stop)
    {
        for (var pass = 1; pass <= 3; pass++)
        {
            stop.ThrowIfCancellationRequested();
            var watch = Stopwatch.StartNew();
            var count = 0;
            foreach (var file in new DirectoryInfo(work).EnumerateFiles())
            {
                // The name and the date come with the listing itself, as the change polling in design 5.3 needs.
                _ = (file.Name, file.LastWriteTimeUtc);
                count++;
            }

            report.Timing($"List names and dates, pass {pass}", count, watch.Elapsed);
            if (count != FileCount)
            {
                report.Check("Listing", false, $"{count} files instead of {FileCount}");
            }
        }
    }

    private static async Task ReadAsync(string[] records, Report report, CancellationToken stop)
    {
        // Each degree reads its own quarter, so no pass reads a file that an earlier pass has read.
        var quarter = records.Length / ParallelReads.Length;
        for (var i = 0; i < ParallelReads.Length; i++)
        {
            var degree = ParallelReads[i];
            var elapsed = await ReadAllAsync(records[(i * quarter)..((i + 1) * quarter)], degree, stop);
            report.Timing(degree == 1 ? "Read one by one" : $"Read {degree} at a time", quarter, elapsed);
        }

        // The first quarter once more, to see whether a second read comes from a cache.
        var again = await ReadAllAsync(records[..quarter], 1, stop);
        report.Timing("Read the first quarter again, one by one", quarter, again);
    }

    private static async Task<TimeSpan> ReadAllAsync(string[] paths, int degree, CancellationToken stop)
    {
        var watch = Stopwatch.StartNew();
        var options = new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = stop };
        await Parallel.ForEachAsync(paths, options, async (path, token) => _ = await File.ReadAllBytesAsync(path, token));
        return watch.Elapsed;
    }

    /// <summary>Design 7.8 relies on the share recording the creating account as the owner.</summary>
    private static void CheckOwner(string what, Func<FileSystemSecurity> readSecurity, Report report)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (readSecurity().GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner)
            {
                report.Check(what, false, "no owner");
                return;
            }

            var isCurrentUser = owner == identity.User;
            report.Check(what, isCurrentUser, isCurrentUser ? "the current user" : $"{Account(owner)}, not the current user");
        }
        catch (Exception ex)
        {
            // Reported, so that the other checks still run.
            report.Check(what, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Account(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
    }

    private static void Cleanup(string work, List<string> created, Report report)
    {
        // If someone swapped the subfolder for a link during the run, the names in it are not ours.
        try
        {
            if (File.GetAttributes(work).HasFlag(FileAttributes.ReparsePoint))
            {
                report.Check("Temporary subfolder removed", false, $"it is now a link, so nothing was deleted: {work}");
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Check("Temporary subfolder removed", false, $"cannot read it, so nothing was deleted: {ex.Message}");
            return;
        }

        var watch = Stopwatch.StartNew();
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

        var elapsed = watch.Elapsed;

        // Not recursive: if anything else ended up in the subfolder, it stays.
        try
        {
            Directory.Delete(work, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Line($"Could not delete the temporary subfolder: {ex.Message}");
        }

        report.Timing("Delete the files", created.Count, elapsed);
        if (failed > 0)
        {
            report.Line($"{failed} files could not be deleted.");
        }

        var removed = !Directory.Exists(work);
        report.Check("Temporary subfolder removed", removed, removed ? null : $"delete it by hand: {work}");
    }

    /// <summary>A made-up record shaped like design 5.2, with random strings in place of the encrypted parts.</summary>
    private static byte[] SampleRecord(int index, int version)
    {
        var level1 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(96));
        var level3 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var json = $$$"""
            {"id":"r{{{index:D4}}}","template":"db","version":{{{version}}},"modifiedBy":"probe.user","modifiedAt":"{{{DateTime.UtcNow:O}}}","public":{"system":"Demo","function":"Probe","env":"test","title":"Probe record {{{index}}}"},"protected":{"1":"{{{level1}}}","3":"{{{level3}}}"}}
            """;
        return Encoding.UTF8.GetBytes(json);
    }
}

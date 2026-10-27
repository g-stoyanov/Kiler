using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.App.Tests;

/// <summary>
/// The run of <c>--diag</c> in a temporary folder on the local disk, with fewer files than the real run. The clock is fake,
/// so every time is 0 ms. A console that acts on a line lets a test change the folder in the middle of a run.
/// </summary>
public sealed class FileDiagTests : IDisposable
{
    private const int Files = 40;
    private const int Part = Files / 4;

    private readonly TempFolder _folder = new();
    private readonly HookedConsole _console = new();
    private readonly DiagReport _report;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 9, 30, 15, TimeSpan.Zero));

    public FileDiagTests() => _report = new DiagReport(_console);

    /// <summary>
    /// True when Windows gives new files to the user who makes them. A process that runs as administrator may give them to the
    /// Administrators group instead, on a local disk, and the owner checks then fail (on a CI machine, for example).
    /// </summary>
    public static bool OwnerIsTheUser
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.Owner == identity.User;
        }
    }

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task Run_ReportsEveryStepInOrder_AndLeavesTheFolderAsItWas()
    {
        var keep = Path.Combine(_folder.Path, "keep.json");
        File.WriteAllText(keep, "{}");

        await Diag().RunAsync(_folder.Path, TestContext.Current.CancellationToken);

        var lines = ConsoleLines();
        Assert.Matches(@"^Temporary subfolder: kiler-diag-20261025-093015-[0-9a-f]{8}$", lines[1]);
        Assert.Matches(@"^Average file size: \d{3} bytes$", lines[3]);
        Assert.Equal(
            [
                $"Test folder: {_folder.Path} (local disk)",
                lines[1],
                $"Write, one by one: {Files} files in 0 ms (0.00 ms per file)",
                lines[3],
                OwnerLine("Owner of the new subfolder"),
                OwnerLine("Owner of a new file"),
                "[OK] Replace by rename over the old file: 0 ms",
                OwnerLine("Owner of the replaced file"),
                $"List names and dates, pass 1: {Files} files in 0 ms (0.00 ms per file)",
                $"List names and dates, pass 2: {Files} files in 0 ms (0.00 ms per file)",
                $"List names and dates, pass 3: {Files} files in 0 ms (0.00 ms per file)",
                $"Read one by one: {Part} files in 0 ms (0.00 ms per file)",
                $"Read 8 at a time: {Part} files in 0 ms (0.00 ms per file)",
                $"Read 16 at a time: {Part} files in 0 ms (0.00 ms per file)",
                $"Read 32 at a time: {Part} files in 0 ms (0.00 ms per file)",
                $"Read the first part again, one by one: {Part} files in 0 ms (0.00 ms per file)",
                $"Delete the files: {Files} files in 0 ms (0.00 ms per file)",
                "[OK] Temporary subfolder removed",
            ],
            lines);
        Assert.Equal(!OwnerIsTheUser, _report.Failed);
        Assert.Equal([keep], Directory.GetFileSystemEntries(_folder.Path));
        Assert.Equal("{}", File.ReadAllText(keep));
    }

    [Fact]
    public async Task Run_WritesMadeUpRecords_AndReplacesOneByRename()
    {
        string? first = null;
        string[] beforeReplace = [];
        string? replaced = null;
        string[] afterReplace = [];
        _console.When("Average file size", () =>
        {
            first = File.ReadAllText(Path.Combine(WorkFolder(), "r0000.json"));
            beforeReplace = FileNames(WorkFolder());
        });
        _console.When("[OK] Replace", () =>
        {
            replaced = File.ReadAllText(Path.Combine(WorkFolder(), "r0000.json"));
            afterReplace = FileNames(WorkFolder());
        });

        await Diag().RunAsync(_folder.Path, TestContext.Current.CancellationToken);

        Assert.Equal([.. Enumerable.Range(0, Files).Select(i => $"r{i:D4}.json")], beforeReplace);
        Assert.Equal(beforeReplace, afterReplace);
        using (var record = JsonDocument.Parse(first!))
        {
            Assert.Equal("r0000", record.RootElement.GetProperty("id").GetString());
            Assert.Equal(1, record.RootElement.GetProperty("version").GetInt32());
            Assert.Equal("2026-10-25T09:30:15.0000000+00:00", record.RootElement.GetProperty("modifiedAt").GetString());
        }

        using (var record = JsonDocument.Parse(replaced!))
        {
            Assert.Equal("r0000", record.RootElement.GetProperty("id").GetString());
            Assert.Equal(2, record.RootElement.GetProperty("version").GetInt32());
        }
    }

    [Fact]
    public async Task Run_AFolderThatDoesNotExist_FailsAndMakesNothing()
    {
        var missing = Path.Combine(_folder.Path, "missing");

        await Diag().RunAsync(missing, TestContext.Current.CancellationToken);

        Assert.Equal([$"[FAIL] Test folder: it does not exist, so give one that does: Kiler.exe --diag <folder> ({missing})"], ConsoleLines());
        Assert.True(_report.Failed);
        Assert.Empty(Directory.GetFileSystemEntries(_folder.Path));
        Assert.Contains("(<test folder>)", _report.ForFile(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_AnInvalidPath_Fails()
    {
        await Diag().RunAsync("bad\0folder", TestContext.Current.CancellationToken);

        Assert.StartsWith("[FAIL] Test folder: ", Assert.Single(ConsoleLines()), StringComparison.Ordinal);
        Assert.True(_report.Failed);
    }

    [Fact]
    public async Task Run_StoppedBeforeItBegins_RemovesTheEmptySubfolder()
    {
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        await Diag().RunAsync(_folder.Path, stop.Token);

        Assert.Equal(["[FAIL] Run: stopped", "Delete the files: no files in 0 ms", "[OK] Temporary subfolder removed"], ConsoleLines()[^3..]);
        Assert.True(_report.Failed);
        Assert.Empty(Directory.GetFileSystemEntries(_folder.Path));
    }

    [Fact]
    public async Task Run_StoppedInTheMiddle_DeletesWhatItMade()
    {
        using var stop = new CancellationTokenSource();
        _console.When("List names and dates, pass 1", stop.Cancel);

        await Diag().RunAsync(_folder.Path, stop.Token);

        Assert.Equal(
            ["[FAIL] Run: stopped", $"Delete the files: {Files} files in 0 ms (0.00 ms per file)", "[OK] Temporary subfolder removed"],
            ConsoleLines()[^3..]);
        Assert.DoesNotContain(ConsoleLines(), line => line.StartsWith("List names and dates, pass 2", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFileSystemEntries(_folder.Path));
    }

    [Fact]
    public async Task Run_AFileOfSomeoneElseInTheSubfolder_StaysThereWithTheSubfolder()
    {
        string? work = null;
        _console.When("Write, one by one", () =>
        {
            work = WorkFolder();
            File.WriteAllText(Path.Combine(work, "other.json"), "{}");
        });

        await Diag().RunAsync(_folder.Path, TestContext.Current.CancellationToken);

        var lines = ConsoleLines();
        Assert.Equal(3, lines.Count(line => line == $"[FAIL] Listing: {Files + 1} files instead of {Files}"));
        Assert.Equal($"Delete the files: {Files} files in 0 ms (0.00 ms per file)", lines[^3]);
        Assert.StartsWith("The temporary subfolder could not be deleted: ", lines[^2], StringComparison.Ordinal);
        Assert.Equal($"[FAIL] Temporary subfolder removed: delete it by hand: {work}", lines[^1]);
        Assert.Equal(["other.json"], FileNames(work!));
    }

    [Fact]
    public async Task Run_NoOneCanRenameTheSubfolderOrAFolderAboveIt_UntilItsFilesAreDeleted()
    {
        // Renamed away, the subfolder could be replaced by a link to another folder, where the run would replace and delete files.
        var root = Directory.CreateDirectory(Path.Combine(_folder.Path, "share")).FullName;
        var refused = new List<string>();
        void TryRenames(string when)
        {
            var work = Assert.Single(Directory.GetDirectories(root, "kiler-diag-*"));
            if (Record.Exception(() => Directory.Move(work, work + "-moved")) is IOException or UnauthorizedAccessException)
            {
                refused.Add($"subfolder {when}");
            }

            if (Record.Exception(() => Directory.Move(root, root + "-moved")) is IOException or UnauthorizedAccessException)
            {
                refused.Add($"folder above {when}");
            }
        }

        _console.When("Write, one by one", () => TryRenames("before the replace"));
        _console.When("Read the first part again", () => TryRenames("before the deletes"));
        _console.When("Delete the files", () => TryRenames("after the deletes"));

        await Diag().RunAsync(root, TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "subfolder before the replace", "folder above before the replace",
                "subfolder before the deletes", "folder above before the deletes",
                "subfolder after the deletes", "folder above after the deletes",
            ],
            refused);
        Assert.Equal("[OK] Temporary subfolder removed", ConsoleLines()[^1]);
        Assert.Empty(Directory.GetFileSystemEntries(root));

        // Let go at the end of the run.
        Directory.Move(root, root + "-moved");
    }

    [Fact]
    public void Constructor_AFileCountThatDoesNotSplitIntoFourParts_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileDiag(_report, _time, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileDiag(_report, _time, 42));
    }

    [Fact]
    public void Location_TellsTheKindOfStorage()
    {
        Assert.Equal("network share (UNC path)", FileDiag.Location(@"\\server\share\Kiler"));
        Assert.Equal("network share (UNC path)", FileDiag.Location(@"\\?\UNC\server\share\Kiler"));
        Assert.Equal("local disk", FileDiag.Location(_folder.Path));
    }

    [Fact]
    public void DescribeOwner_TheCurrentUser_IsOk()
    {
        var user = new SecurityIdentifier("S-1-5-21-1000-2000-3000-1001");

        Assert.Equal((true, "the current user"), FileDiag.DescribeOwner(user, user, user, _report));
    }

    [Fact]
    public void DescribeOwner_TheOwnerThatTheProcessGivesNewFiles_FailsAndSaysToRunAsANormalUser()
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var user = new SecurityIdentifier("S-1-5-21-1000-2000-3000-1001");

        var (ok, detail) = FileDiag.DescribeOwner(administrators, user, administrators, _report);

        Assert.False(ok);
        Assert.Equal(
            $"{Name(administrators)}, not the current user. Kiler runs as administrator, which gives new files to that owner; run it as a normal user",
            detail);
    }

    [Fact]
    public void DescribeOwner_AnOwnerThatTheStorageGaveTheFile_FailsWithoutBlamingTheProcess()
    {
        // A share that records the Administrators group for a user who does not run as administrator.
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var user = new SecurityIdentifier("S-1-5-21-1000-2000-3000-1001");

        Assert.Equal((false, $"{Name(administrators)}, not the current user"), FileDiag.DescribeOwner(administrators, user, user, _report));
    }

    [Theory]
    [InlineData("S-1-5-21-1000-2000-3000-1105")] // a made-up account of a domain or a machine
    [InlineData("S-1-12-1-1111-2222-3333-4444")] // a made-up Microsoft Entra account
    [InlineData("S-1-22-1-1000")] // a made-up user of a NAS, without a Windows account
    [InlineData("S-1-22-2-1000")] // a made-up group of a NAS
    [InlineData("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")] // NT SERVICE\TrustedInstaller: a service can have an internal name
    public void DescribeOwner_AnotherAccount_IsNamedOnTheConsoleButNotInTheFile(string sid)
    {
        var other = new SecurityIdentifier(sid);
        var user = new SecurityIdentifier("S-1-5-21-1000-2000-3000-1001");

        var (ok, detail) = FileDiag.DescribeOwner(other, user, user, _report);
        _report.Line(detail);

        Assert.False(ok);
        Assert.Equal($"{Name(other)}, not the current user", detail);
        Assert.Equal($"<another account>, not the current user{Environment.NewLine}", _report.ForFile());
    }

    [Fact]
    public void DescribeOwner_ARealAccountOfThisMachineOrDomain_IsNotNamedInTheFile()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var otherUser = new SecurityIdentifier("S-1-5-21-1000-2000-3000-1001");

        var (ok, detail) = FileDiag.DescribeOwner(identity.User!, otherUser, otherUser, _report);
        _report.Line(detail);

        Assert.False(ok);
        Assert.Equal($"{identity.Name}, not the current user", detail);
        Assert.Equal($"<another account>, not the current user{Environment.NewLine}", _report.ForFile());
    }

    [Theory]
    [InlineData("S-1-5-18")] // SYSTEM
    [InlineData("S-1-5-19")] // LOCAL SERVICE
    [InlineData("S-1-5-20")] // NETWORK SERVICE
    [InlineData("S-1-5-32-544")] // BUILTIN\Administrators
    [InlineData("S-1-5-32-545")] // BUILTIN\Users
    public void DescribeOwner_AnAccountOfWindowsItself_IsNamedInTheFileToo(string sid)
    {
        var windows = new SecurityIdentifier(sid);
        var user = new SecurityIdentifier("S-1-5-21-1000-2000-3000-1001");

        var (ok, detail) = FileDiag.DescribeOwner(windows, user, user, _report);
        _report.Line(detail);

        Assert.False(ok);
        Assert.Equal($"{Name(windows)}, not the current user", detail);
        Assert.Equal($"{detail}{Environment.NewLine}", _report.ForFile());
    }

    /// <summary>The line of an owner check, as the process gives new files their owner (<see cref="OwnerIsTheUser"/>).</summary>
    public static string OwnerLine(string what)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.Owner == identity.User
            ? $"[OK] {what}: the current user"
            : $"[FAIL] {what}: {Name(identity.Owner!)}, not the current user. Kiler runs as administrator, which gives new files to that owner; run it as a normal user";
    }

    /// <summary>The name that Windows gives the account, or the SID when it has none.</summary>
    private static string Name(SecurityIdentifier sid)
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

    private static string[] FileNames(string folder) => [.. Directory.GetFiles(folder).Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal)];

    private FileDiag Diag() => new(_report, _time, Files);

    /// <summary>The temporary subfolder of the run: the one folder in the test folder whose name it gave.</summary>
    private string WorkFolder() => Assert.Single(Directory.GetDirectories(_folder.Path, "kiler-diag-*"));

    private string[] ConsoleLines() => _console.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A console that runs an action when a line that starts with a prefix is written.</summary>
    private sealed class HookedConsole : StringWriter
    {
        private readonly List<(string Prefix, Action Action)> _hooks = [];

        public void When(string prefix, Action action) => _hooks.Add((prefix, action));

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            foreach (var (prefix, action) in _hooks)
            {
                if (value?.StartsWith(prefix, StringComparison.Ordinal) == true)
                {
                    action();
                }
            }
        }
    }
}

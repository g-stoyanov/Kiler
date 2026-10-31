using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Storage.Tests;

/// <summary>
/// The audit of the secret values in the data layer (M6.17, design 12): a line appended to
/// <c>audit\&lt;day&gt;\&lt;account&gt;.jsonl</c> through the local copy, straight to the share, and the lines of the days of a
/// span read from the share. The layer knows nothing of what a line says.
/// </summary>
public sealed class AuditTests : IDisposable
{
    private const string Me = "demo.admin";
    private const string Other = "demo.user";

    private static readonly DateOnly Day = new(2026, 10, 31);

    private readonly TestFolder _test = new();
    private readonly TestFileSystem _files = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly DataFolder _share;
    private readonly DataCopy _copy;

    public AuditTests()
    {
        _share = DataFolder.Create(Path.Combine(_test.Root, "data"), _files);
        _copy = DataCopy.Open(_share, Directory.CreateDirectory(Path.Combine(_test.Root, "copy")).FullName, _clock, Me);
    }

    public void Dispose() => _test.Dispose();

    [Fact]
    public void AnAppend_MakesTheDayFolderAndTheFile_AndASecondLineGoesAfterTheFirst()
    {
        Assert.Equal(WriteOutcome.Written, _copy.AppendAudit(Day, Me, Line("first")));
        Assert.Equal("first\n", Text(Day, Me));

        Assert.Equal(WriteOutcome.Written, _copy.AppendAudit(Day, Me, Line("second")));
        Assert.Equal(WriteOutcome.Written, _copy.AppendAudit(Day, Other, Line("theirs")));

        Assert.Equal("first\nsecond\n", Text(Day, Me));
        Assert.Equal("theirs\n", Text(Day, Other));
        Assert.Equal(["demo.admin.jsonl", "demo.user.jsonl"], Directory.GetFiles(DayFolder(Day)).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Appends_OnTwoDays_GoIntoTwoFolders()
    {
        Assert.Equal(WriteOutcome.Written, _copy.AppendAudit(Day, Me, Line("today")));
        Assert.Equal(WriteOutcome.Written, _copy.AppendAudit(Day.AddDays(1), Me, Line("tomorrow")));

        Assert.Equal(["2026-10-31", "2026-11-01"], Directory.GetDirectories(Path.Combine(_share.Root, DataFolder.Audit)).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("today\n", Text(Day, Me));
        Assert.Equal("tomorrow\n", Text(Day.AddDays(1), Me));
    }

    /// <summary>
    /// Another Kiler of the same account can append between the open and the write: the line goes at the end of the file as it
    /// is then, never over what the other wrote.
    /// </summary>
    [Fact]
    public void AnAppend_GoesAtTheEndOfTheFile_AlsoAfterAnotherKilerAppendedSinceItOpened()
    {
        Assert.Equal(WriteOutcome.Written, _copy.AppendAudit(Day, Me, Line("first")));
        _files.Before = (step, _) =>
        {
            if (step == nameof(IFileSystem.Append))
            {
                _files.Before = null;
                using var other = new FileStream(PathOf(Day, Me), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                other.Write("other\n"u8);
            }
        };

        Assert.Equal(WriteOutcome.Written, _copy.AppendAudit(Day, Me, Line("mine")));

        Assert.Equal("first\nother\nmine\n", Text(Day, Me));
    }

    [Fact]
    public void WhileOffline_AnAppendAndARead_AreRefusedBeforeTheyTouchTheShare_AndGoThroughOnceTheShareIsBack()
    {
        Assert.Equal(WriteOutcome.Written, _copy.AppendAudit(Day, Me, Line("first")));
        var tried = GoAway();
        Assert.ThrowsAny<IOException>(() => _copy.Load());
        Assert.True(_copy.Offline, "The copy did not go offline.");
        var before = tried.Count;

        Assert.Equal(WriteOutcome.Offline, _copy.AppendAudit(Day, Me, Line("second")));
        Assert.Throws<OfflineException>(() => _copy.ReadAudit(Day, Day));

        Assert.Equal(before, tried.Count);
        _files.Before = null;
        _copy.Load();
        Assert.Equal(WriteOutcome.Written, _copy.AppendAudit(Day, Me, Line("second")));
        Assert.Equal(["2026-10-31 demo.admin: first|second"], Shown(_copy.ReadAudit(Day, Day)));
    }

    [Fact]
    public void AnAppend_ThroughALinkAtTheAuditFolder_IsRefused_AndWritesNothingThere()
    {
        var elsewhere = Elsewhere();
        var audit = Path.Combine(_share.Root, DataFolder.Audit);
        Directory.Delete(audit);
        TestFolder.CreateJunction(audit, elsewhere);

        Assert.ThrowsAny<IOException>(() => _copy.AppendAudit(Day, Me, Line("first")));

        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
    }

    [Fact]
    public void AnAppend_ThroughALinkAtTheDayFolder_IsRefused_AndWritesNothingThere()
    {
        var elsewhere = Elsewhere();
        TestFolder.CreateJunction(DayFolder(Day), elsewhere);

        Assert.ThrowsAny<IOException>(() => _copy.AppendAudit(Day, Me, Line("first")));

        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
    }

    /// <summary>
    /// A new day folder is empty while it is held, so anyone who can write it can make it a mount point in place (M2.3): the
    /// append checks the held folders after the open, before the write, and writes nothing through it.
    /// </summary>
    [Fact]
    public void AnAppend_ThroughADayFolderMadeAMountPointInPlace_WritesNothingThere()
    {
        var elsewhere = Elsewhere();
        _files.Before = (step, _) =>
        {
            if (step == nameof(IFileSystem.OpenToAppend))
            {
                _files.Before = null;
                TestFolder.MakeMountPoint(DayFolder(Day), elsewhere);
            }
        };

        Assert.ThrowsAny<IOException>(() => _copy.AppendAudit(Day, Me, Line("first")));

        Assert.All(Directory.GetFiles(elsewhere), file => Assert.Equal(0, new FileInfo(file).Length));
    }

    [Fact]
    public void AnAppend_ToALinkAtTheFile_IsRefused_AndLeavesItsTargetAsItIs()
    {
        var target = Path.Combine(Elsewhere(), "target.txt");
        File.WriteAllText(target, "kept\n");
        Directory.CreateDirectory(DayFolder(Day));
        TestFolder.CreateFileLink(PathOf(Day, Me), target);

        Assert.ThrowsAny<IOException>(() => _copy.AppendAudit(Day, Me, Line("first")));

        Assert.Equal("kept\n", File.ReadAllText(target));
    }

    /// <summary>
    /// A hard link has neither the attributes of a link nor of a folder: it is another name of a file anywhere on the volume,
    /// which the append would write with the user's rights (security review).
    /// </summary>
    [Fact]
    public void AnAppend_ToAHardLinkAtTheFile_IsRefused_AndLeavesTheOtherFileAsItIs()
    {
        var target = Path.Combine(Elsewhere(), "target.txt");
        File.WriteAllText(target, "kept\n");
        Directory.CreateDirectory(DayFolder(Day));
        TestFolder.CreateHardLink(PathOf(Day, Me), target);

        Assert.ThrowsAny<IOException>(() => _copy.AppendAudit(Day, Me, Line("first")));

        Assert.Equal("kept\n", File.ReadAllText(target));
    }

    /// <summary>A link whose target is missing is opened as itself, so the append never makes its target (the M2.3 note on create-new).</summary>
    [Fact]
    public void AnAppend_ToALinkAtTheFileWhoseTargetIsMissing_IsRefused_AndMakesNoTarget()
    {
        var target = Path.Combine(Elsewhere(), "target.txt");
        Directory.CreateDirectory(DayFolder(Day));
        TestFolder.CreateFileLink(PathOf(Day, Me), target);

        Assert.ThrowsAny<IOException>(() => _copy.AppendAudit(Day, Me, Line("first")));

        Assert.False(File.Exists(target), "The append made the target of the link.");
    }

    [Theory]
    [InlineData("an account in another case")]
    [InlineData("an account with a backslash")]
    [InlineData("an empty account")]
    [InlineData("an empty line")]
    [InlineData("a line with a line end")]
    [InlineData("a line over the cap")]
    public void AnAppend_OfABadAccountOrLine_IsRefused_BeforeAnythingIsMade(string what)
    {
        var (account, line) = what switch
        {
            "an account in another case" => ("Demo.Admin", Line("first")),
            "an account with a backslash" => (@"..\demo.admin", Line("first")),
            "an empty account" => ("", Line("first")),
            "an empty line" => (Me, []),
            "a line with a line end" => (Me, Line("first\nsecond")),
            "a line over the cap" => (Me, new byte[DataFolder.MaxAuditLine + 1]),
            _ => throw new ArgumentOutOfRangeException(nameof(what)),
        };

        Assert.Throws<ArgumentException>(() => _copy.AppendAudit(Day, account, line));

        Assert.False(Directory.Exists(DayFolder(Day)), "A refused append made the day folder.");
    }

    [Fact]
    public void AnAppend_OfALineOfTheCap_IsTaken()
    {
        var line = Enumerable.Repeat((byte)'x', DataFolder.MaxAuditLine).ToArray();

        Assert.Equal(WriteOutcome.Written, _copy.AppendAudit(Day, Me, line));

        Assert.Equal(DataFolder.MaxAuditLine + 1, new FileInfo(PathOf(Day, Me)).Length);
    }

    [Fact]
    public void TheRead_GivesTheLinesOfTheDaysOfItsSpan_DayByDayAndAccountByAccount()
    {
        Append(Day.AddDays(-1), Me, "before");
        Append(Day, Other, "b1");
        Append(Day, Me, "a1");
        Append(Day, Me, "a2");
        Append(Day.AddDays(1), Me, "next");
        Append(Day.AddDays(2), Me, "after");

        var read = _copy.ReadAudit(Day, Day.AddDays(1));

        Assert.Equal(["2026-10-31 demo.admin: a1|a2", "2026-10-31 demo.user: b1", "2026-11-01 demo.admin: next"], Shown(read));
        Assert.Empty(read.Skipped);
        Assert.Equal(0, read.MoreSkipped);
    }

    [Fact]
    public void TheRead_OfDaysWithoutAFolder_OrWithoutTheAuditFolder_GivesNothing()
    {
        Append(Day, Me, "a1");

        Assert.Empty(_copy.ReadAudit(Day.AddDays(1), Day.AddDays(3)).Files);
        Directory.Delete(Path.Combine(_share.Root, DataFolder.Audit), recursive: true);
        var read = _copy.ReadAudit(Day, Day);
        Assert.Empty(read.Files);
        Assert.Empty(read.Skipped);
    }

    /// <summary>A line that another Kiler is writing, or one that a write cut, has no line end yet, and is left out.</summary>
    [Fact]
    public void TheRead_LeavesOutALastLineWithoutALineEnd()
    {
        Directory.CreateDirectory(DayFolder(Day));
        File.WriteAllBytes(PathOf(Day, Me), "one\ntwo\nhalf"u8.ToArray());

        var read = _copy.ReadAudit(Day, Day);

        Assert.Equal(["2026-10-31 demo.admin: one|two"], Shown(read));
        Assert.Empty(read.Skipped);
    }

    [Fact]
    public void TheRead_OfAFileOverTheCap_GivesItsWholeLinesWithinTheCap_AndReportsIt()
    {
        // Lines of 100 bytes with their line ends: the cap ends in the middle of one.
        var line = new string('x', 99) + "\n";
        var count = (DataFolder.MaxAuditFile / line.Length) + 2;
        Directory.CreateDirectory(DayFolder(Day));
        File.WriteAllText(PathOf(Day, Me), string.Concat(Enumerable.Repeat(line, count)));

        var read = _copy.ReadAudit(Day, Day);

        var file = Assert.Single(read.Files);
        Assert.Equal(DataFolder.MaxAuditFile / line.Length, file.Lines.Count);
        Assert.All(file.Lines, taken => Assert.Equal(99, taken.Length));
        Assert.Equal([new SkippedEntry(@"audit\2026-10-31", "demo.admin.jsonl", SkipReason.OverBudget)], read.Skipped);
    }

    /// <summary>
    /// A bad name, and a file that cannot be read, are reported each for itself, and the rest is read. A file of another kind
    /// and a folder are not the audit's, and are left out.
    /// </summary>
    [Fact]
    public void TheRead_ReportsABadNameAndAFileThatCannotBeRead_AndReadsTheRest()
    {
        Append(Day, Me, "a1");
        Append(Day, "demo.denied", "hidden");
        TestFolder.DenyRead(PathOf(Day, "demo.denied"));
        File.WriteAllText(Path.Combine(DayFolder(Day), "Demo.Upper.jsonl"), "upper\n");
        File.WriteAllText(Path.Combine(DayFolder(Day), "demo.case.JSONL"), "case\n");
        File.WriteAllText(Path.Combine(DayFolder(Day), "notes.txt"), "notes\n");
        Directory.CreateDirectory(Path.Combine(DayFolder(Day), "demo.folder.jsonl"));

        try
        {
            var read = _copy.ReadAudit(Day, Day);

            Assert.Equal(["2026-10-31 demo.admin: a1"], Shown(read));
            Assert.Equal(
                [
                    new SkippedEntry(@"audit\2026-10-31", "Demo.Upper.jsonl", SkipReason.BadName),
                    new SkippedEntry(@"audit\2026-10-31", "demo.case.JSONL", SkipReason.BadName),
                    new SkippedEntry(@"audit\2026-10-31", "demo.denied.jsonl", SkipReason.Unreadable),
                ],
                read.Skipped.OrderBy(skipped => skipped.Name, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            TestFolder.AllowRead(PathOf(Day, "demo.denied"));
        }
    }

    [Fact]
    public void TheRead_ReportsAFileThatIsALink_AndReadsTheRest()
    {
        Append(Day, Me, "a1");
        var target = Path.Combine(Elsewhere(), "target.jsonl");
        File.WriteAllText(target, "elsewhere\n");
        TestFolder.CreateFileLink(PathOf(Day, "demo.link"), target);

        var read = _copy.ReadAudit(Day, Day);

        Assert.Equal(["2026-10-31 demo.admin: a1"], Shown(read));
        Assert.Equal([new SkippedEntry(@"audit\2026-10-31", "demo.link.jsonl", SkipReason.Link)], read.Skipped);
    }

    [Fact]
    public void TheRead_ReportsADayFolderThatIsALink_AndReadsTheOtherDays()
    {
        Append(Day, Me, "a1");
        var elsewhere = Elsewhere();
        File.WriteAllText(Path.Combine(elsewhere, "demo.admin.jsonl"), "elsewhere\n");
        TestFolder.CreateJunction(DayFolder(Day.AddDays(1)), elsewhere);

        var read = _copy.ReadAudit(Day, Day.AddDays(1));

        Assert.Equal(["2026-10-31 demo.admin: a1"], Shown(read));
        Assert.Equal([new SkippedEntry(@"audit\2026-11-01", null, SkipReason.Link)], read.Skipped);
    }

    [Fact]
    public void TheRead_ReportsAnAuditFolderThatIsALink_AndGivesNothing()
    {
        var elsewhere = Elsewhere();
        Directory.CreateDirectory(Path.Combine(elsewhere, "2026-10-31"));
        File.WriteAllText(Path.Combine(elsewhere, "2026-10-31", "demo.admin.jsonl"), "elsewhere\n");
        var audit = Path.Combine(_share.Root, DataFolder.Audit);
        Directory.Delete(audit);
        TestFolder.CreateJunction(audit, elsewhere);

        var read = _copy.ReadAudit(Day, Day);

        Assert.Empty(read.Files);
        Assert.Equal([new SkippedEntry("audit", null, SkipReason.Link)], read.Skipped);
    }

    /// <summary>
    /// An empty day folder that is made a mount point in place while it is held, before its listing, gives the files of
    /// another folder: the read checks it after the reads, gives nothing of it and reports it.
    /// </summary>
    [Fact]
    public void TheRead_OfADayFolderMadeAMountPointInPlace_GivesNothingOfIt_AndReportsIt()
    {
        var elsewhere = Elsewhere();
        File.WriteAllText(Path.Combine(elsewhere, "demo.admin.jsonl"), "elsewhere\n");
        Directory.CreateDirectory(DayFolder(Day));
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.List) && path == DayFolder(Day))
            {
                _files.Before = null;
                TestFolder.MakeMountPoint(DayFolder(Day), elsewhere);
            }
        };

        var read = _copy.ReadAudit(Day, Day);

        Assert.Empty(read.Files);
        Assert.Equal([new SkippedEntry(@"audit\2026-10-31", null, SkipReason.Link)], read.Skipped);
    }

    /// <summary>
    /// An empty <c>audit\</c> that is made a mount point in place while it is held gives the day folders of another folder: the
    /// read checks it after the reads of the days, and throws, as a load does for its root.
    /// </summary>
    [Fact]
    public void TheRead_OfAnAuditFolderMadeAMountPointInPlace_Throws()
    {
        var elsewhere = Elsewhere();
        Directory.CreateDirectory(Path.Combine(elsewhere, "2026-10-31"));
        File.WriteAllText(Path.Combine(elsewhere, "2026-10-31", "demo.admin.jsonl"), "elsewhere\n");
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.HoldFolder) && path == DayFolder(Day))
            {
                _files.Before = null;
                TestFolder.MakeMountPoint(Path.Combine(_share.Root, DataFolder.Audit), elsewhere);
            }
        };

        Assert.ThrowsAny<IOException>(() => _copy.ReadAudit(Day, Day));
    }

    [Fact]
    public void TheRead_OfASpanBackwardsOrOfMoreDaysThanTheCap_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _copy.ReadAudit(Day, Day.AddDays(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => _copy.ReadAudit(Day, Day.AddDays(DataFolder.MaxAuditDays)));

        Assert.Empty(_copy.ReadAudit(Day, Day.AddDays(DataFolder.MaxAuditDays - 1)).Files);
    }

    /// <summary>
    /// Anyone who can write the share can put any number of files there, so a read takes at most so much memory: the bytes of
    /// each file, before they are read, and a cost for each of its lines, as a file of empty lines would otherwise take some
    /// 16 times its bytes (security review). One file a day, so the order of the takes is that of the days.
    /// </summary>
    [Fact]
    public void TheRead_TakesAtMostItsBudget_TheBytesOfTheFilesAndACostForEachLine()
    {
        for (var day = 0; day < 3; day++)
        {
            Append(Day.AddDays(day), Me, "123456789");
        }

        // Each file has 10 bytes and one line of 16 more, so a budget of 60 takes two of them.
        var read = _share.ReadAudit(Day, Day.AddDays(2), maxEntries: 10, maxReported: 10, maxBytes: 60, maxFiles: 10);

        Assert.Equal(["2026-10-31 demo.admin: 123456789", "2026-11-01 demo.admin: 123456789"], Shown(read));
        Assert.Equal([new SkippedEntry(@"audit\2026-11-02", "demo.admin.jsonl", SkipReason.OverBudget)], read.Skipped);

        // Ten empty lines are 10 bytes and 160 more.
        Directory.CreateDirectory(DayFolder(Day.AddDays(3)));
        File.WriteAllText(PathOf(Day.AddDays(3), Me), new string('\n', 10));
        var empty = _share.ReadAudit(Day.AddDays(3), Day.AddDays(3), maxEntries: 10, maxReported: 10, maxBytes: 169, maxFiles: 10);
        Assert.Empty(empty.Files);
        Assert.Equal([new SkippedEntry(@"audit\2026-11-03", "demo.admin.jsonl", SkipReason.OverBudget)], empty.Skipped);
        var taken = _share.ReadAudit(Day.AddDays(3), Day.AddDays(3), maxEntries: 10, maxReported: 10, maxBytes: 170, maxFiles: 10);
        Assert.Equal(10, Assert.Single(taken.Files).Lines.Count);
    }

    /// <summary>A read opens at most so many files, as it holds the lock of the copy (security review). One file a day, as above.</summary>
    [Fact]
    public void TheRead_OpensAtMostItsCapOfFiles()
    {
        for (var day = 0; day < 3; day++)
        {
            Append(Day.AddDays(day), Me, "x");
        }

        var read = _share.ReadAudit(Day, Day.AddDays(2), maxEntries: 10, maxReported: 10, maxBytes: 1000, maxFiles: 2);

        Assert.Equal(["2026-10-31 demo.admin: x", "2026-11-01 demo.admin: x"], Shown(read));
        Assert.Equal([new SkippedEntry(@"audit\2026-11-02", "demo.admin.jsonl", SkipReason.OverBudget)], read.Skipped);
    }

    /// <summary>A day folder that lists too many entries gives none, and the reports have a cap, with the rest counted.</summary>
    [Fact]
    public void TheRead_KeepsItsListingCapAndItsReportCap()
    {
        for (var day = 0; day < 3; day++)
        {
            for (var i = 0; i < 6; i++)
            {
                Append(Day.AddDays(day), "demo.many" + i.ToString(CultureInfo.InvariantCulture), "x");
            }
        }

        var read = _share.ReadAudit(Day, Day.AddDays(2), maxEntries: 5, maxReported: 2, maxBytes: 1000, maxFiles: 100);

        Assert.Empty(read.Files);
        Assert.Equal([new SkippedEntry(@"audit\2026-10-31", null, SkipReason.OverBudget), new SkippedEntry(@"audit\2026-11-01", null, SkipReason.OverBudget)], read.Skipped);
        Assert.Equal(1, read.MoreSkipped);
    }

    private static byte[] Line(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>The files of a read as text: the day, the account and the lines.</summary>
    private static string[] Shown(AuditRead read) =>
        [.. read.Files.Select(file => $"{file.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} {file.Account}: {string.Join("|", file.Lines.Select(line => Encoding.UTF8.GetString(line.Span)))}")];

    private void Append(DateOnly day, string account, string line) => Assert.Equal(WriteOutcome.Written, _copy.AppendAudit(day, account, Line(line)));

    private string DayFolder(DateOnly day) => Path.Combine(_share.Root, DataFolder.Audit, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private string PathOf(DateOnly day, string account) => Path.Combine(DayFolder(day), account + ".jsonl");

    private string Text(DateOnly day, string account) => File.ReadAllText(PathOf(day, account));

    private string Elsewhere() => Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;

    /// <summary>Takes the share away: every step of the file system on it fails, as when the network has gone. Gives the steps that were tried on it.</summary>
    private ConcurrentQueue<string> GoAway()
    {
        var tried = new ConcurrentQueue<string>();
        _files.Before = (step, path) =>
        {
            if (path.StartsWith(_share.Root, StringComparison.OrdinalIgnoreCase))
            {
                tried.Enqueue(step);
                throw new IOException("The test took the share away.");
            }
        };

        return tried;
    }
}

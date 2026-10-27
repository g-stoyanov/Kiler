using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Storage.Tests;

/// <summary>The local copy of a data folder, the start from it and the load of the share (M2.7).</summary>
public sealed class DataCopyTests : IDisposable
{
    private static readonly DocumentKey Record = DocumentKey.InFolder(DataFolder.Records, "8f3a1c");
    private static readonly DocumentKey OtherRecord = DocumentKey.InFolder(DataFolder.Records, "b72e09");
    private static readonly DocumentKey User = DocumentKey.InFolder(DataFolder.Users, "demo.user");
    private static readonly DocumentKey Template = DocumentKey.InFolder(DataFolder.Templates, "db");
    private static readonly DocumentKey Stash = DocumentKey.InFolder(DataFolder.Stashes, "a1");
    private static readonly DocumentKey Settings = DocumentKey.AtRoot(DataFolder.Settings);

    private readonly TestFolder _test = new();
    private readonly TestFileSystem _files = new();

    /// <summary>The clock of the copy in the tests that time how long a document was in it, from now on.</summary>
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly DataFolder _share;
    private readonly string _copy;

    public DataCopyTests()
    {
        _share = DataFolder.Create(Path.Combine(_test.Root, "data"), _files);
        _copy = Directory.CreateDirectory(Path.Combine(_test.Root, "copy")).FullName;
    }

    public void Dispose() => _test.Dispose();

    [Fact]
    public void Load_WithAnEmptyLocalCopy_ReadsEveryDocument_AndKeepsItsBytes()
    {
        DocumentKey[] all = [Settings, Template, Stash, User, Record, OtherRecord];
        foreach (var key in all)
        {
            Write(key, "Демо " + key.Id);
        }

        var copy = Open();
        Assert.Empty(copy.Documents());
        _files.Reads = 0;

        var load = copy.Load();

        Assert.Equal(all.Length, _files.Reads);
        Assert.Equal(Keys(all), Keys(load.Changed));
        Assert.DoesNotContain(load.Changed, document => document.Read.IsDamaged);
        Assert.Equal("Демо 8f3a1c", (string?)load.Changed.Single(document => document.Key == Record).Read.Document?.Content["name"]);
        Assert.Empty(load.Gone);
        Assert.Empty(load.Skipped);
        foreach (var key in all)
        {
            Assert.Equal(File.ReadAllBytes(_share.DocumentPath(key)), File.ReadAllBytes(CopyPath(key)));
        }

        Assert.True(File.Exists(Path.Combine(_copy, "index.json")), "The local copy has no index.");
    }

    [Fact]
    public void Load_Twice_ReadsNothingTheSecondTime()
    {
        Write(Record, "first");
        Write(User, "user");
        var copy = Open();
        copy.Load();
        _files.Reads = 0;

        var load = copy.Load();

        Assert.Equal(0, _files.Reads);
        Assert.Empty(load.Changed);
        Assert.Empty(load.Gone);
    }

    [Fact]
    public void Load_AfterARestart_ReadsOnlyTheNewAndChangedDocuments()
    {
        Write(Record, "first");
        Write(OtherRecord, "other");
        Write(User, "user");
        Open().Load();
        Assert.Equal(WriteOutcome.Written, _share.ReplaceDocument(Record, 1, Content("changed")).Outcome);
        Write(Template, "new");
        _files.Reads = 0;

        var load = Open().Load();

        // The slot of the version that the changed one replaced comes from the copy, not from the share (M2.7a).
        Assert.Equal(2, _files.Reads);
        Assert.Equal(Keys([Template, Record]), Keys(load.Changed));
        var record = load.Changed.Single(document => document.Key == Record).Read.Document;
        Assert.Equal(2, record?.Version);
        Assert.Equal("changed", (string?)record?.Content["name"]);
        Assert.Equal(File.ReadAllBytes(_share.DocumentPath(Record)), File.ReadAllBytes(CopyPath(Record)));
    }

    [Fact]
    public void Documents_AfterARestart_ComeFromTheLocalCopy_WithoutTheShare()
    {
        var content = new JsonObject { ["template"] = "db", ["name"] = "Демо база" };
        TestFolder.WriteAsKiler(_share.DocumentPath(Record), DocumentFormat.Next(Record, 4, content));
        Write(Settings, "settings");
        Open().Load();
        var steps = 0;
        _files.Before = (_, _) => Interlocked.Increment(ref steps);

        var documents = Open().Documents();

        Assert.Equal(0, steps);
        Assert.Equal(Keys([Settings, Record]), Keys(documents));
        var record = documents.Single(document => document.Key == Record).Read;
        Assert.Equal(5, record.Document?.Version);
        Assert.True(JsonNode.DeepEquals(content, record.Document?.Content), record.Document?.Content.ToJsonString());
    }

    [Fact]
    public void Documents_GiveEachCallerItsOwnContent()
    {
        Write(Record, "first");
        var copy = Open();
        copy.Load();

        var first = copy.Documents().Single().Read.Document!;
        first.Content["name"] = "changed by the caller";

        Assert.Equal("first", (string?)copy.Documents().Single().Read.Document?.Content["name"]);
    }

    [Fact]
    public void Load_KeepsTheOwnerOfEachFile_ForTheNextStart()
    {
        Write(Record, "mine");
        Write(OtherRecord, "theirs");
        Write(User, "unknown");
        _files.Owners.Set(_share.DocumentPath(OtherRecord), TestOwners.Other);
        _files.Owners.Set(_share.DocumentPath(User), null);

        var load = Open().Load();
        var documents = Open().Documents();

        foreach (var given in new[] { load.Changed, documents })
        {
            Assert.Equal(TestOwners.Me, given.Single(document => document.Key == Record).Read.Owner);
            Assert.Equal(TestOwners.Other, given.Single(document => document.Key == OtherRecord).Read.Owner);
            Assert.Null(given.Single(document => document.Key == User).Read.Owner);
        }
    }

    [Fact]
    public void Load_Of2000Documents_ReadsAtMost16AtATime_AndLoadsThemAll()
    {
        WriteRecords(2000);

        // A read that takes a while, as over the network, so the reads meet.
        _files.Before = (step, _) =>
        {
            if (step == nameof(IFileSystem.OpenRead))
            {
                Thread.Sleep(1);
            }
        };

        var load = Open().Load();

        Assert.Equal(2000, load.Changed.Count);
        Assert.DoesNotContain(load.Changed, document => document.Read.IsDamaged);
        Assert.Equal(16, _files.MostReadsAtOnce);
    }

    [Fact]
    public void Load_Of2000Documents_WritesItsTimesToTheOutput()
    {
        WriteRecords(2000);

        var clock = Stopwatch.StartNew();
        var first = Open().Load();
        var firstTime = clock.Elapsed;
        clock.Restart();
        var copy = Open();
        var documents = copy.Documents();
        var startTime = clock.Elapsed;
        _files.Reads = 0;
        clock.Restart();
        var second = copy.Load();
        var secondTime = clock.Elapsed;

        Assert.Equal(2000, first.Changed.Count);
        Assert.Equal(2000, documents.Count);
        Assert.Empty(second.Changed);
        Assert.Equal(0, _files.Reads);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"2000 documents: the first load {firstTime.TotalMilliseconds:F0} ms, the start from the local copy {startTime.TotalMilliseconds:F0} ms, the second load {secondTime.TotalMilliseconds:F0} ms.");
    }

    [Fact]
    public void Load_SkipsLinksAndBadNames_AndReportsThemEscaped()
    {
        Write(Record, "good");
        var records = Path.Combine(_share.Root, DataFolder.Records);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        File.WriteAllBytes(Path.Combine(elsewhere, "b72e09.json"), DocumentFormat.New(OtherRecord, Content("elsewhere")));
        TestFolder.CreateJunction(Path.Combine(records, "b72e09.json"), elsewhere);
        // A right-to-left override, so the name shows as "aexe.json".
        var reversed = "a" + (char)0x202E + "nosj.exe";
        foreach (var name in new[] { "Demo", reversed, "запис", new string('A', 100) })
        {
            File.WriteAllText(Path.Combine(records, name + ".json"), "{}");
        }

        var load = Open().Load();

        Assert.Equal(Record, Assert.Single(load.Changed).Key);
        Assert.Equal(new SkippedEntry(DataFolder.Records, "b72e09.json", SkipReason.Link), Assert.Single(load.Skipped, entry => entry.Reason == SkipReason.Link));
        string[] escaped =
        [
            "Demo.json",
            "a" + StorageNameTests.Code(0x202E) + "nosj.exe.json",
            string.Concat("запис".Select(letter => StorageNameTests.Code(letter))) + ".json",
            new string('A', StorageName.MaxLength) + "...",
        ];
        Assert.Equal(
            escaped.Order(StringComparer.Ordinal),
            load.Skipped.Where(entry => entry.Reason == SkipReason.BadName).Select(entry => entry.Name).Order(StringComparer.Ordinal));
        Assert.All(load.Skipped, entry => Assert.Equal(DataFolder.Records, entry.Folder));
        Assert.Equal(["8f3a1c.json"], Directory.GetFiles(Path.GetDirectoryName(CopyPath(Record))!).Select(Path.GetFileName));
    }

    /// <summary>
    /// A file held without sharing (a sharing violation), one that an ACL denies (access denied), and one whose bytes are
    /// locked, so its open goes through and its read fails (the M2.2 note).
    /// </summary>
    [Theory]
    [InlineData("held")]
    [InlineData("denied")]
    [InlineData("locked")]
    public void Load_AFileThatCannotBeOpenedOrRead_IsReported_AndTheOthersLoad(string how)
    {
        Write(Record, "first");
        Write(OtherRecord, "other");
        var other = _share.DocumentPath(OtherRecord);
        var copy = Open();
        LoadResult load;
        if (how == "denied")
        {
            TestFolder.DenyRead(other);
            load = copy.Load();
            TestFolder.AllowRead(other);
        }
        else
        {
            using var holder = new FileStream(other, FileMode.Open, FileAccess.ReadWrite, how == "held" ? FileShare.None : FileShare.ReadWrite | FileShare.Delete);
            // CA1416 does not take Assert.Skip for the end of the method, so the call stands inside the guard.
            if (how == "locked" && OperatingSystem.IsWindows())
            {
                holder.Lock(0, holder.Length);
            }
            else if (how == "locked")
            {
                Assert.Skip("A range of a file is locked through Windows.");
            }

            load = copy.Load();
        }

        Assert.Equal(Record, Assert.Single(load.Changed).Key);
        Assert.Equal(new SkippedEntry(DataFolder.Records, "b72e09.json", SkipReason.Unreadable), Assert.Single(load.Skipped));
        Assert.False(File.Exists(CopyPath(OtherRecord)));

        Assert.Equal(OtherRecord, Assert.Single(copy.Load().Changed).Key);
    }

    [Fact]
    public void Load_ADocumentWhoseCopyCannotBeWritten_IsGivenAndReported_AndTheNextLoadReadsItAgain()
    {
        Write(Template, "first");
        Write(Record, "first");
        var copy = Open();
        copy.Load();
        Assert.Equal(WriteOutcome.Written, _share.ReplaceDocument(Template, 1, Content("changed")).Outcome);
        Assert.Equal(WriteOutcome.Written, _share.ReplaceDocument(Record, 1, Content("changed")).Outcome);
        LoadResult load;

        // A program that holds the file of the copy, as an antivirus can; the templates come before the records.
        using (new FileStream(CopyPath(Template), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            load = copy.Load();
        }

        Assert.Equal(Keys([Template, Record]), Keys(load.Changed));
        Assert.Equal(new SkippedEntry(DataFolder.Templates, "db.json", SkipReason.NotCopied), Assert.Single(load.Skipped));
        var documents = Open().Documents();
        Assert.Equal(1, documents.Single(document => document.Key == Template).Read.Document?.Version);
        Assert.Equal(2, documents.Single(document => document.Key == Record).Read.Document?.Version);

        _files.Reads = 0;
        Assert.Equal(Template, Assert.Single(copy.Load().Changed).Key);
        Assert.Equal(1, _files.Reads);
        Assert.Equal(File.ReadAllBytes(_share.DocumentPath(Template)), File.ReadAllBytes(CopyPath(Template)));
    }

    [Fact]
    public void Load_WhenTheIndexCannotBeWritten_IsReportedOnce_AndTheNextLoadWritesIt()
    {
        Write(Record, "first");
        var copy = Open();
        copy.Load();
        Write(User, "new");
        LoadResult load;
        using (new FileStream(Path.Combine(_copy, "index.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            load = copy.Load();
        }

        Assert.Equal(User, Assert.Single(load.Changed).Key);
        Assert.Equal(new SkippedEntry(null, null, SkipReason.NotCopied), Assert.Single(load.Skipped));
        Assert.DoesNotContain("demo.user", File.ReadAllText(Path.Combine(_copy, "index.json")), StringComparison.Ordinal);

        var next = copy.Load();

        Assert.Empty(next.Changed);
        Assert.Empty(next.Skipped);
        Assert.Equal(Keys([User, Record]), Keys(Open().Documents()));
    }

    [Fact]
    public void Load_AsksForTheFoldersAsOften_HoweverManyDocumentsItReads()
    {
        WriteRecords(10);
        var few = RequestsForFolders(DataCopy.Open(_share, NewCopyFolder("few"), TimeProvider.System));
        WriteRecords(200);
        var many = RequestsForFolders(DataCopy.Open(_share, NewCopyFolder("many"), TimeProvider.System));

        Assert.Equal(few, many);
    }

    [Fact]
    public void Load_NewDocumentsBeyondTheBudgetOfDocuments_AreNotRead_AndAreReported()
    {
        Write(Settings, "settings");
        WriteRecords(4);
        var copy = Open(Limits(documents: 3));
        _files.Reads = 0;

        var load = copy.Load();

        Assert.Equal(3, load.Changed.Count);
        Assert.Equal(3, _files.Reads);
        Assert.Contains(load.Changed, document => document.Key == Settings);
        Assert.Equal(2, load.Skipped.Count(entry => entry is { Folder: DataFolder.Records, Reason: SkipReason.OverBudget }));
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(CopyPath(Record))!).Length);

        var again = copy.Load();

        Assert.Empty(again.Changed);
        Assert.Equal(2, again.Skipped.Count(entry => entry.Reason == SkipReason.OverBudget));
    }

    [Fact]
    public void Load_NewDocumentsBeyondTheBudgetOfBytes_AreNotRead_AndAreReported()
    {
        WriteRecords(4);
        var size = new FileInfo(_share.DocumentPath(DocumentKey.InFolder(DataFolder.Records, "r0000"))).Length;

        var load = Open(Limits(bytes: (2 * size) + (size / 2))).Load();

        Assert.Equal(2, load.Changed.Count);
        Assert.Equal(2, load.Skipped.Count(entry => entry.Reason == SkipReason.OverBudget));
    }

    [Fact]
    public void Load_AChangedDocumentThatNoLongerFitsTheBudget_KeepsTheVersionOfTheCopy_AndIsReported()
    {
        Write(Record, "small");
        var copy = Open(Limits(bytes: new FileInfo(_share.DocumentPath(Record)).Length + 100));
        copy.Load();
        Assert.Equal(WriteOutcome.Written, _share.ReplaceDocument(Record, 1, Content(new string('x', 1000))).Outcome);
        _files.Reads = 0;

        var load = copy.Load();

        Assert.Equal(0, _files.Reads);
        Assert.Empty(load.Changed);
        Assert.Equal(new SkippedEntry(DataFolder.Records, "8f3a1c.json", SkipReason.OverBudget), Assert.Single(load.Skipped));
        Assert.Equal("small", (string?)Assert.Single(Open().Documents()).Read.Document?.Content["name"]);
    }

    [Fact]
    public void Load_AFileListedOverTheSizeLimit_IsDamagedWithoutARead_AndCostsNoBudget()
    {
        Write(Record, "good");
        using (var file = File.Create(_share.DocumentPath(OtherRecord)))
        {
            file.Write("""{"id": "b72e09", "version": 1}"""u8);
            file.SetLength(DocumentFormat.MaxSize + 1);
        }

        var size = new FileInfo(_share.DocumentPath(Record)).Length;

        var load = Open(Limits(bytes: size)).Load();

        Assert.Equal(DocumentDamage.TooLarge, load.Changed.Single(document => document.Key == OtherRecord).Read.Damage);
        Assert.False(load.Changed.Single(document => document.Key == Record).Read.IsDamaged);
        Assert.Equal(size, _files.BytesRead);
    }

    [Fact]
    public void Load_AFileThatGrewSinceItsListing_IsNotReadUntilTheNextLoad()
    {
        Write(Record, "first");
        var path = _share.DocumentPath(Record);
        var grown = false;
        _files.Before = (step, at) =>
        {
            if (step == nameof(IFileSystem.OpenRead) && at == path && !grown)
            {
                grown = true;
                File.WriteAllBytes(path, DocumentFormat.New(Record, Content(new string('x', 5000))));
            }
        };
        var copy = Open();

        var load = copy.Load();

        Assert.Empty(load.Changed);
        Assert.Equal(new SkippedEntry(DataFolder.Records, "8f3a1c.json", SkipReason.Unreadable), Assert.Single(load.Skipped));
        Assert.Equal(0, _files.BytesRead);
        Assert.Equal(5000, ((string?)Assert.Single(copy.Load().Changed).Read.Document?.Content["name"])?.Length);
    }

    /// <summary>
    /// Anyone who can write the share adds documents, and takes them away before the next load, again and again: the last
    /// good bytes that the copy keeps of them count, so they do not fill the disk (second security review of M2.7), and as
    /// they were in the copy only briefly, they make room for the new documents once they are gone (third security review).
    /// </summary>
    [Fact]
    public void Load_TheLastGoodBytesOfGoneDocuments_CountInTheBudget_AndMakeRoomWhenTheyStayedBriefly()
    {
        WriteRecords(2);
        var copy = OpenAt(Limits(bytes: (2 * RecordSize) + (RecordSize / 2)));
        copy.Load();
        DeleteRecords();
        WriteRecord(9000);
        WriteRecord(9001);
        _clock.Advance(TimeSpan.FromMinutes(1));

        var load = copy.Load();

        Assert.Equal(2, load.Gone.Count);
        Assert.Empty(load.Changed);
        Assert.Equal(2, load.Skipped.Count(entry => entry.Reason == SkipReason.OverBudget));
        Assert.Equal(["r0000.json", "r0001.json"], CopyRecords());
        _clock.Advance(TimeSpan.FromMinutes(1));

        var next = copy.Load();

        Assert.Empty(next.Gone);
        Assert.Equal(2, next.Changed.Count);
        Assert.Empty(next.Skipped);
        Assert.Equal(["r9000.json", "r9001.json"], CopyRecords());
        Assert.Equal(2, OpenAt().Documents().Count);
    }

    /// <summary>The last good bytes of real documents, which were in the copy for days, keep their place for a restore.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_TheLastGoodBytesOfDocumentsThatStayedADay_KeepTheirPlace(bool byDocuments)
    {
        WriteRecords(2);
        var copy = OpenAt(byDocuments ? Limits(documents: 2) : Limits(bytes: (2 * RecordSize) + (RecordSize / 2)));
        copy.Load();
        _clock.Advance(TimeSpan.FromDays(2));
        DeleteRecords();
        copy.Load();
        WriteRecord(9000);
        _clock.Advance(TimeSpan.FromMinutes(1));

        var load = copy.Load();

        Assert.Empty(load.Changed);
        Assert.Equal(new SkippedEntry(DataFolder.Records, "r9000.json", SkipReason.OverBudget), Assert.Single(load.Skipped));
        Assert.Equal(["r0000.json", "r0001.json"], CopyRecords());
    }

    /// <summary>
    /// Junk that someone puts back for one load a day after it went, and takes away again, was on the share for minutes, so
    /// its last good bytes still make room (fourth security review of M2.7).
    /// </summary>
    [Fact]
    public void Load_ADocumentListedAgainAfterItWent_CountsOnlyTheTimeTheShareListedIt()
    {
        WriteRecords(2);
        var copy = OpenAt(Limits(bytes: (2 * RecordSize) + (RecordSize / 2)));
        copy.Load();
        _clock.Advance(TimeSpan.FromMinutes(1));
        DeleteRecords();
        copy.Load();
        _clock.Advance(TimeSpan.FromDays(2));
        WriteRecords(2);
        Assert.Equal(2, copy.Load().Changed.Count);
        _clock.Advance(TimeSpan.FromMinutes(1));
        DeleteRecords();
        copy.Load();
        WriteRecord(9000);
        _clock.Advance(TimeSpan.FromMinutes(1));

        var load = copy.Load();

        Assert.Equal("r9000", Assert.Single(load.Changed).Key.Id);
        Assert.Empty(load.Skipped);
    }

    /// <summary>Of two brief leftovers, the one that went first makes room, and only as many as it takes go.</summary>
    [Fact]
    public void Load_TheBriefLeftoverThatWentFirst_MakesRoomFirst_AndOnlyAsManyAsItTakes()
    {
        WriteRecords(3);
        var copy = OpenAt(Limits(bytes: (3 * RecordSize) + (RecordSize / 2)));
        copy.Load();
        _clock.Advance(TimeSpan.FromMinutes(1));
        File.Delete(_share.DocumentPath(DocumentKey.InFolder(DataFolder.Records, "r0001")));
        copy.Load();
        _clock.Advance(TimeSpan.FromMinutes(1));
        File.Delete(_share.DocumentPath(DocumentKey.InFolder(DataFolder.Records, "r0000")));
        copy.Load();
        WriteRecord(9000);
        _clock.Advance(TimeSpan.FromMinutes(1));

        var load = copy.Load();

        Assert.Equal("r9000", Assert.Single(load.Changed).Key.Id);
        Assert.Equal(["r0000.json", "r0002.json", "r9000.json"], CopyRecords());
    }

    [Fact]
    public void Load_ABriefLeftoverMakesRoomInTheBudgetOfDocuments()
    {
        WriteRecords(2);
        var copy = OpenAt(Limits(documents: 2));
        copy.Load();
        _clock.Advance(TimeSpan.FromMinutes(1));
        DeleteRecords();
        copy.Load();
        WriteRecord(9000);
        _clock.Advance(TimeSpan.FromMinutes(1));

        var load = copy.Load();

        Assert.Equal("r9000", Assert.Single(load.Changed).Key.Id);
        var left = CopyRecords();
        Assert.Equal(2, left.Length);
        Assert.Contains("r9000.json", left);
    }

    [Fact]
    public void Load_ADocumentThatWouldNotFitEvenWithTheBriefLeftoversGone_TakesNoneOfThem()
    {
        WriteRecords(2);
        var copy = OpenAt(Limits(bytes: (2 * RecordSize) + (RecordSize / 2)));
        copy.Load();
        _clock.Advance(TimeSpan.FromMinutes(1));
        File.Delete(_share.DocumentPath(DocumentKey.InFolder(DataFolder.Records, "r0000")));
        copy.Load();
        var large = DocumentKey.InFolder(DataFolder.Records, "r8999");
        TestFolder.WriteAsKiler(_share.DocumentPath(large), DocumentFormat.New(large, Content(new string('x', (int)(3 * RecordSize)))));
        _clock.Advance(TimeSpan.FromMinutes(1));

        var load = copy.Load();

        Assert.Equal(new SkippedEntry(DataFolder.Records, "r8999.json", SkipReason.OverBudget), Assert.Single(load.Skipped));
        Assert.Equal(["r0000.json", "r0001.json"], CopyRecords());
    }

    /// <summary>The leftovers that a load planned to make room with stay when the folder then cannot be taken.</summary>
    [Fact]
    public void Load_WhenTheFolderIsMadeAMountPointAfterTheLeftoversWerePlannedToGo_KeepsThem()
    {
        Write(Record, "record");
        var size = new FileInfo(_share.DocumentPath(Record)).Length;
        var copy = OpenAt(Limits(bytes: size + (size / 2)));
        copy.Load();
        _clock.Advance(TimeSpan.FromMinutes(1));
        File.Delete(_share.DocumentPath(Record));
        copy.Load();
        Write(OtherRecord, "record");
        var records = Path.Combine(_share.Root, DataFolder.Records);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        File.Copy(_share.DocumentPath(OtherRecord), Path.Combine(elsewhere, "b72e09.json"));
        TestFolder.SkipWithoutJunctions(records);
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.OpenRead) && path == _share.DocumentPath(OtherRecord) && !File.GetAttributes(records).HasFlag(FileAttributes.ReparsePoint))
            {
                File.Delete(path);
                TestFolder.MakeMountPoint(records, elsewhere);
            }
        };
        _clock.Advance(TimeSpan.FromMinutes(1));

        var load = copy.Load();

        Assert.Equal(new SkippedEntry(DataFolder.Records, null, SkipReason.Unreadable), Assert.Single(load.Skipped));
        Assert.True(File.Exists(KeptPath(Record)), "The leftover went for a folder that was not taken.");
    }

    [Fact]
    public void Load_ADocumentWhoseCopyCouldNotBeReadAtTheStart_IsWrittenAgain_AlsoWithTheSameBytes()
    {
        Write(Record, "record");
        Open().Load();
        var copy = Open();
        using (new FileStream(CopyPath(Record), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(copy.Documents());
        }

        LoadResult load;

        // Held without delete sharing, the file of the copy cannot be replaced, so the write is reported.
        using (new FileStream(CopyPath(Record), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            load = copy.Load();
        }

        Assert.Equal(new SkippedEntry(DataFolder.Records, "8f3a1c.json", SkipReason.NotCopied), Assert.Single(load.Skipped));
    }

    /// <summary>A document that people change every day is in the copy from when the copy first took it, not from its last change.</summary>
    [Fact]
    public void Load_ADocumentChangedJustBeforeItWent_StillCountsTheDaysItWasInTheCopy()
    {
        WriteRecords(2);
        var copy = OpenAt(Limits(bytes: (2 * RecordSize) + (RecordSize / 2)));
        copy.Load();
        _clock.Advance(TimeSpan.FromDays(2));
        var changed = DocumentKey.InFolder(DataFolder.Records, "r0000");
        TestFolder.WriteAsKiler(_share.DocumentPath(changed), DocumentFormat.Next(changed, 1, new JsonObject { ["name"] = "Демо запис 0000", ["notes"] = new string('y', 1000) }));
        Assert.Equal(changed, Assert.Single(copy.Load().Changed).Key);
        _clock.Advance(TimeSpan.FromMinutes(1));
        DeleteRecords();
        copy.Load();
        WriteRecord(9000);
        _clock.Advance(TimeSpan.FromMinutes(1));

        var load = copy.Load();

        Assert.Equal(new SkippedEntry(DataFolder.Records, "r9000.json", SkipReason.OverBudget), Assert.Single(load.Skipped));
        Assert.Equal(["r0000.json", "r0001.json"], CopyRecords());
    }

    /// <summary>
    /// The brief leftovers go only as far as it takes, and not at all when even all of them would not make the document fit.
    /// </summary>
    [Fact]
    public void Load_TheLastGoodBytesOfBriefDocuments_GoOnlyWhenTheyMakeRoom()
    {
        WriteRecords(2);
        var copy = OpenAt(Limits(bytes: (2 * RecordSize) + (RecordSize / 2)));
        copy.Load();
        _clock.Advance(TimeSpan.FromMinutes(1));
        File.Delete(_share.DocumentPath(DocumentKey.InFolder(DataFolder.Records, "r0000")));
        copy.Load();
        WriteRecord(9000);

        // Too large to fit even with the leftover gone, and listed before the one that fits (NTFS lists names in order).
        var large = DocumentKey.InFolder(DataFolder.Records, "r8999");
        TestFolder.WriteAsKiler(_share.DocumentPath(large), DocumentFormat.New(large, Content(new string('x', (int)(3 * RecordSize)))));
        _clock.Advance(TimeSpan.FromMinutes(1));

        var load = copy.Load();

        Assert.Equal("r9000", Assert.Single(load.Changed).Key.Id);
        Assert.Equal(new SkippedEntry(DataFolder.Records, "r8999.json", SkipReason.OverBudget), Assert.Single(load.Skipped));
        Assert.Equal(["r0001.json", "r9000.json"], CopyRecords());
    }

    [Fact]
    public void Load_TheLastGoodBytesOfADamagedDocument_CountInTheBudget()
    {
        Write(Record, "good");
        var size = new FileInfo(_share.DocumentPath(Record)).Length;
        var copy = Open(Limits(bytes: size + (size / 2)));
        copy.Load();
        File.WriteAllText(_share.DocumentPath(Record), "{", Encoding.UTF8);
        Write(OtherRecord, "good");

        var load = copy.Load();

        Assert.Equal(DocumentDamage.NotJson, Assert.Single(load.Changed).Read.Damage);
        Assert.Equal(new SkippedEntry(DataFolder.Records, "b72e09.json", SkipReason.OverBudget), Assert.Single(load.Skipped));

        // Also after a start.
        Assert.Equal(new SkippedEntry(DataFolder.Records, "b72e09.json", SkipReason.OverBudget), Assert.Single(Open(Limits(bytes: size + (size / 2))).Load().Skipped));
    }

    /// <summary>A document that could not be written into the copy is in memory all the same (second security review of M2.7).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_ADocumentThatCouldNotBeWrittenIntoTheCopy_CountsInTheBudgetOfTheLoad(bool byDocuments)
    {
        Write(Template, "same size");
        Write(Record, "same size");
        var size = Math.Max(new FileInfo(_share.DocumentPath(Template)).Length, new FileInfo(_share.DocumentPath(Record)).Length);

        // A folder where the file of the template goes, so its write fails; the templates come before the records.
        Directory.CreateDirectory(CopyPath(Template));

        var load = Open(byDocuments ? Limits(documents: 1) : Limits(bytes: size + (size / 2))).Load();

        Assert.Equal(Template, Assert.Single(load.Changed).Key);
        Assert.Contains(new SkippedEntry(DataFolder.Templates, "db.json", SkipReason.NotCopied), load.Skipped);
        Assert.Contains(new SkippedEntry(DataFolder.Records, "8f3a1c.json", SkipReason.OverBudget), load.Skipped);
    }

    [Fact]
    public void Load_TheBudgetOfBytesSpansTheFolders()
    {
        TestFolder.WriteAsKiler(_share.DocumentPath(Settings), DocumentFormat.New(Settings, new JsonObject { ["notes"] = new string('x', 1000) }));
        WriteRecords(2);
        var size = new FileInfo(_share.DocumentPath(DocumentKey.InFolder(DataFolder.Records, "r0000"))).Length;
        var settings = new FileInfo(_share.DocumentPath(Settings)).Length;

        var load = Open(Limits(bytes: settings + size + (size / 2))).Load();

        Assert.Equal(2, load.Changed.Count);
        Assert.Contains(load.Changed, document => document.Key == Settings);
        Assert.Equal(1, load.Skipped.Count(entry => entry is { Folder: DataFolder.Records, Reason: SkipReason.OverBudget }));
    }

    [Fact]
    public void Load_NewDocumentsBesideTheOnesOfTheCopy_FitOnlyInWhatIsLeft()
    {
        WriteRecords(2);
        var size = new FileInfo(_share.DocumentPath(DocumentKey.InFolder(DataFolder.Records, "r0000"))).Length;
        var copy = Open(Limits(bytes: (2 * size) + (size / 2)));
        Assert.Equal(2, copy.Load().Changed.Count);
        WriteRecords(3);

        var load = copy.Load();

        Assert.Equal(new SkippedEntry(DataFolder.Records, "r0002.json", SkipReason.OverBudget), Assert.Single(load.Skipped));
    }

    [Fact]
    public void Load_AChangedDocumentThatGrowsWithinTheBudget_IsRead()
    {
        Write(Record, "small");
        var size = new FileInfo(_share.DocumentPath(Record)).Length;
        var copy = Open(Limits(bytes: size + (size / 2)));
        copy.Load();
        Assert.Equal(WriteOutcome.Written, _share.ReplaceDocument(Record, 1, Content("small, and a little more")).Outcome);

        var load = copy.Load();

        Assert.Equal(Record, Assert.Single(load.Changed).Key);
        Assert.Empty(load.Skipped);
    }

    [Fact]
    public void Load_ADocumentThatComesBackAsItWas_IsGivenAgain()
    {
        Write(Record, "record");
        var copy = Open();
        copy.Load();
        var away = Path.Combine(_test.Root, "8f3a1c.json");
        File.Move(_share.DocumentPath(Record), away);
        Assert.Equal(Record, Assert.Single(copy.Load().Gone));
        File.Move(away, _share.DocumentPath(Record));

        var load = copy.Load();

        Assert.Equal(Record, Assert.Single(load.Changed).Key);
        Assert.Equal(Record, Assert.Single(Open().Documents()).Key);
    }

    [Fact]
    public void Load_ADocumentWhoseBytesHaveNotChanged_IsNotWrittenIntoTheCopyAgain()
    {
        Write(Record, "record");
        var copy = Open();
        copy.Load();
        File.SetLastWriteTimeUtc(_share.DocumentPath(Record), DateTime.UtcNow.AddMinutes(1));
        LoadResult load;

        // Held without delete sharing, the file of the copy cannot be replaced, so a write would be reported.
        using (new FileStream(CopyPath(Record), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            load = copy.Load();
        }

        Assert.Equal(Record, Assert.Single(load.Changed).Key);
        Assert.Empty(load.Skipped);
        Assert.Empty(copy.Load().Changed);
    }

    [Fact]
    public void Load_AFolderOfMoreEntriesThanTheLimit_IsReported_AndItsDocumentsStay()
    {
        // The root holds the 8 folders of the layout.
        WriteRecords(3);
        var copy = Open(Limits(listed: 9));
        copy.Load();
        WriteRecords(10);
        _files.Reads = 0;

        var load = copy.Load();

        Assert.Equal(0, _files.Reads);
        Assert.Empty(load.Changed);
        Assert.Empty(load.Gone);
        Assert.Equal(new SkippedEntry(DataFolder.Records, null, SkipReason.OverBudget), Assert.Single(load.Skipped));
        Assert.Equal(3, Open().Documents().Count);
    }

    [Fact]
    public void Load_MoreSkippedEntriesThanTheLimit_AreCounted()
    {
        Write(Record, "good");
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(_share.Root, DataFolder.Records, $"Bad{i}.json"), "{}");
        }

        var load = Open(Limits(reports: 2)).Load();

        Assert.Equal(2, load.Skipped.Count);
        Assert.Equal(3, load.MoreSkipped);
        Assert.All(load.Skipped, entry => Assert.Equal(SkipReason.BadName, entry.Reason));
    }

    [Fact]
    public void Load_ADamagedDocument_IsGivenAsDamaged_AndItsBytesAreNotKept()
    {
        File.WriteAllText(_share.DocumentPath(Record), "{\"id\": \"8f3a1c\", \"version\": ", Encoding.UTF8);

        var load = Open().Load();

        Assert.Equal(DocumentDamage.NotJson, Assert.Single(load.Changed).Read.Damage);
        Assert.False(File.Exists(CopyPath(Record)));

        _files.Reads = 0;
        var again = Open();
        Assert.Equal(DocumentDamage.NotJson, Assert.Single(again.Documents()).Read.Damage);
        Assert.Empty(again.Load().Changed);
        Assert.Equal(0, _files.Reads);
    }

    [Fact]
    public void Load_ADocumentDamagedAfterALoad_LeavesItsGoodBytesInTheLocalCopy()
    {
        Write(Record, "good");
        var copy = Open();
        copy.Load();
        var good = File.ReadAllBytes(CopyPath(Record));
        File.WriteAllText(_share.DocumentPath(Record), "{", Encoding.UTF8);

        var load = copy.Load();

        Assert.Equal(DocumentDamage.NotJson, Assert.Single(load.Changed).Read.Damage);
        Assert.Equal(good, File.ReadAllBytes(KeptPath(Record)));

        // A start gives the share as it is, and the last good bytes stay.
        Assert.Equal(DocumentDamage.NotJson, Assert.Single(Open().Documents()).Read.Damage);
        Assert.Equal(good, File.ReadAllBytes(KeptPath(Record)));
    }

    [Fact]
    public void Load_ADocumentGoneFromTheShare_IsGivenAsGone_AndIsNotGivenAgain()
    {
        Write(Record, "gone");
        Write(OtherRecord, "stays");
        var copy = Open();
        copy.Load();
        File.Delete(_share.DocumentPath(Record));

        var load = copy.Load();

        Assert.Equal(Record, Assert.Single(load.Gone));
        Assert.Empty(load.Changed);
        Assert.Equal(OtherRecord, Assert.Single(Open().Documents()).Key);
        Assert.Empty(copy.Load().Gone);
        Assert.True(File.Exists(KeptPath(Record)), "The last good copy of the gone document is not in the copy.");
    }

    [Fact]
    public void Load_AMissingDocumentFolder_GivesItsDocumentsAsGone()
    {
        Write(Stash, "stash");
        Write(Record, "record");
        var copy = Open();
        copy.Load();
        Directory.Delete(Path.Combine(_share.Root, DataFolder.Stashes), recursive: true);

        var load = copy.Load();

        Assert.Equal(Stash, Assert.Single(load.Gone));
        Assert.Empty(load.Skipped);
    }

    [Fact]
    public void Load_ADocumentFolderThatIsALink_IsReported_AndNotFollowed_AndItsDocumentsStay()
    {
        Write(Record, "record");
        Write(User, "user");
        var copy = Open();
        copy.Load();
        var records = Path.Combine(_share.Root, DataFolder.Records);
        var elsewhere = Path.Combine(_test.Root, "elsewhere");
        TestFolder.SkipWithoutJunctions(records);
        Directory.Move(records, elsewhere);
        File.WriteAllBytes(Path.Combine(elsewhere, "b72e09.json"), DocumentFormat.New(OtherRecord, Content("elsewhere")));
        TestFolder.CreateJunction(records, elsewhere);
        _files.Reads = 0;

        var load = copy.Load();

        Assert.Equal(0, _files.Reads);
        Assert.Empty(load.Changed);
        Assert.Empty(load.Gone);
        Assert.Contains(new SkippedEntry(null, "records", SkipReason.Link), load.Skipped);
        Assert.Contains(new SkippedEntry(DataFolder.Records, null, SkipReason.Unreadable), load.Skipped);
        Assert.Equal(Keys([User, Record]), Keys(Open().Documents()));
    }

    [Fact]
    public void Load_WhenAFolderIsMadeAMountPointDuringTheReads_IsReported_AndNothingOfItIsTaken()
    {
        Write(Record, "record");
        var records = Path.Combine(_share.Root, DataFolder.Records);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        File.WriteAllBytes(Path.Combine(elsewhere, "8f3a1c.json"), DocumentFormat.New(Record, Content("elsewhere")));
        TestFolder.SkipWithoutJunctions(records);
        _files.Before = (step, path) =>
        {
            // The folder is held and its only file goes, so it is empty and can be made a mount point in place (M2.3).
            if (step == nameof(IFileSystem.OpenRead) && path == _share.DocumentPath(Record) && !File.GetAttributes(records).HasFlag(FileAttributes.ReparsePoint))
            {
                File.Delete(path);
                TestFolder.MakeMountPoint(records, elsewhere);
            }
        };

        var load = Open().Load();

        Assert.Equal(new SkippedEntry(DataFolder.Records, null, SkipReason.Unreadable), Assert.Single(load.Skipped));
        Assert.Empty(load.Changed);
        Assert.False(File.Exists(CopyPath(Record)));
    }

    [Fact]
    public void Load_WhenTheRootIsGone_Throws_AndKeepsTheLocalCopy()
    {
        Write(Record, "record");
        var copy = Open();
        copy.Load();
        Directory.Move(_share.Root, _share.Root + "-moved");

        Assert.Throws<DirectoryNotFoundException>(() => copy.Load());
        Assert.Equal(Record, Assert.Single(Open().Documents()).Key);
    }

    [Fact]
    public void Load_AnErrorThatIsNotOfAFile_IsThrown_AndNothingOfTheFolderIsTaken()
    {
        WriteRecords(40);
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.OpenRead) && path.EndsWith("r0020.json", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A made-up fault of the code.");
            }
        };

        Assert.Throws<InvalidOperationException>(() => Open().Load());
        Assert.Empty(Open().Documents());
    }

    [Fact]
    public void Load_DeletesTheOldTemporaryFilesOfItsListings()
    {
        Write(Record, "record");
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(11));
        var records = Path.Combine(_share.Root, DataFolder.Records);
        var old = Path.Combine(records, "8f3a1c.0123abcd.tmp");
        var oldAtTheRoot = Path.Combine(_share.Root, "settings.89abcdef.tmp");
        var young = Path.Combine(records, "8f3a1c.4567cdef.tmp");
        foreach (var file in new[] { old, oldAtTheRoot, young })
        {
            File.WriteAllText(file, "made up");
        }

        File.SetLastWriteTimeUtc(young, time.GetUtcNow().UtcDateTime.AddMinutes(-1));

        var load = DataCopy.Open(_share, _copy, time).Load();

        Assert.False(File.Exists(old), "The old temporary file is still there.");
        Assert.False(File.Exists(oldAtTheRoot), "The old temporary file at the root is still there.");
        Assert.True(File.Exists(young), "The young temporary file is gone.");
        Assert.Equal(Record, Assert.Single(load.Changed).Key);
        Assert.Empty(load.Skipped);
    }

    [Fact]
    public void Open_WithADamagedIndex_ReadsEverythingAgain()
    {
        Write(Record, "record");
        Write(User, "user");
        Open().Load();
        File.WriteAllText(Path.Combine(_copy, "index.json"), "{\"format\": 1, \"files\": [", Encoding.UTF8);
        _files.Reads = 0;

        var copy = Open();

        Assert.Empty(copy.Documents());
        Assert.Equal(Keys([User, Record]), Keys(copy.Load().Changed));
        Assert.Equal(2, _files.Reads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Documents_AFileOfTheLocalCopyThatIsDamagedOrMissing_IsLeftOut_AndTheLoadReadsItAgain(bool damaged)
    {
        Write(Record, "record");
        Write(User, "user");
        Open().Load();
        if (damaged)
        {
            File.WriteAllText(CopyPath(Record), "{", Encoding.UTF8);
        }
        else
        {
            File.Delete(CopyPath(Record));
        }

        _files.Reads = 0;
        var copy = Open();

        Assert.Equal(User, Assert.Single(copy.Documents()).Key);
        Assert.Equal(Record, Assert.Single(copy.Load().Changed).Key);
        Assert.Equal(1, _files.Reads);
        Assert.Equal(File.ReadAllBytes(_share.DocumentPath(Record)), File.ReadAllBytes(CopyPath(Record)));
    }

    [Fact]
    public void Documents_AFileOfTheLocalCopyWithAnotherVersionThanItsIndex_IsLeftOut()
    {
        Write(Record, "record");
        Open().Load();
        File.WriteAllBytes(CopyPath(Record), DocumentFormat.Next(Record, 1, Content("other")));

        Assert.Empty(Open().Documents());
    }

    /// <summary>
    /// What a crash between the files and the index leaves: bytes that another file on the share had at the same version,
    /// which must not get the owner of the entry (security review of M2.7).
    /// </summary>
    [Fact]
    public void Documents_AFileOfTheLocalCopyWithOtherBytesAtTheSameVersion_IsLeftOut()
    {
        Write(Record, "record");
        Open().Load();
        File.WriteAllBytes(CopyPath(Record), DocumentFormat.New(Record, Content("other")));

        Assert.Empty(Open().Documents());
    }

    [Fact]
    public void Open_AnIndexWithANameThatFailsTheCheck_LeavesThatEntryOut()
    {
        Write(Record, "record");
        Open().Load();
        File.WriteAllBytes(Path.Combine(_copy, "outside.json"), DocumentFormat.New(Record, Content("outside")));
        ChangeIndex(index => index["files"]![0]!["id"] = @"..\..\outside");

        var copy = Open();

        Assert.Empty(copy.Documents());
        Assert.Equal(Record, Assert.Single(copy.Load().Changed).Key);
    }

    /// <summary>
    /// A folder that is not a document folder and a root document that the data folder does not have, each with the file
    /// that its path in the copy would name; no version, a size below zero, no hash or not the hash, and a damage that Kiler
    /// does not know.
    /// </summary>
    [Theory]
    [InlineData("""{"folder": "history"}""", @"history\8f3a1c.json")]
    [InlineData("""{"folder": null}""", "8f3a1c.json")]
    [InlineData("""{"version": 0}""", null)]
    [InlineData("""{"size": -1}""", null)]
    [InlineData("""{"hash": null}""", null)]
    [InlineData("""{"hash": "00"}""", null)]
    [InlineData("""{"damage": 99}""", null)]
    [InlineData("""{"copied": -1}""", null)]
    [InlineData("""{"copied": 2000000}""", null)]
    public void Open_AnIndexEntryThatIsNotWhole_IsLeftOut_AndItsDocumentIsReadAgain(string change, string? file)
    {
        Write(Record, "record");
        Open().Load();
        ChangeIndex(index =>
        {
            var entry = index["files"]![0]!.AsObject();
            foreach (var (name, value) in JsonNode.Parse(change)!.AsObject())
            {
                entry[name] = value?.DeepClone();
            }
        });
        if (file is not null)
        {
            var path = Path.Combine(_copy, "data", file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(CopyPath(Record), path);
        }

        var copy = Open();

        Assert.Empty(copy.Documents());
        Assert.Equal(Record, Assert.Single(copy.Load().Changed).Key);
    }

    /// <summary>
    /// An index with more than twice the entries of the budget is not what the copy wrote, and bounds the memory; one a
    /// little over the budget, as a fault could leave it, is taken, as no index would cost the last good bytes (fourth
    /// security review of M2.7a).
    /// </summary>
    [Fact]
    public void Open_AnIndexWithMoreThanTwiceTheEntriesOfTheBudget_IsNotTaken_AndOneALittleOverIs()
    {
        Write(Record, "record");
        Write(User, "user");
        Write(Template, "template");
        Open().Load();

        Assert.Equal(3, DataCopy.Open(_share, _copy, TimeProvider.System, Limits(documents: 2)).Documents().Count);
        Assert.Empty(DataCopy.Open(_share, _copy, TimeProvider.System, Limits(documents: 1)).Documents());
    }

    [Fact]
    public void Open_DeletesTheFilesOfTheCopyThatNoEntryNames_AndKeepsTheLastGoodBytesOfGoneDocuments()
    {
        Write(Record, "record");
        Write(OtherRecord, "gone");
        var copy = Open();
        copy.Load();
        File.Delete(_share.DocumentPath(OtherRecord));
        copy.Load();
        var records = Path.GetDirectoryName(CopyPath(Record))!;
        string[] strays = [Path.Combine(records, "a1b2c3.json"), Path.Combine(records, "notes.txt"), Path.Combine(_copy, "data", "levels.json")];
        foreach (var stray in strays)
        {
            File.WriteAllText(stray, "made up");
        }

        // Young, it may be the write of another Kiler of the user, in another session.
        var young = Path.Combine(records, "d4e5f6.json");
        File.WriteAllText(young, "made up");
        _clock.Advance(TimeSpan.FromMinutes(11));
        File.SetLastWriteTimeUtc(young, _clock.GetUtcNow().UtcDateTime);

        OpenAt();

        Assert.All(strays, stray => Assert.False(File.Exists(stray), $"{Path.GetFileName(stray)} is still there."));
        Assert.True(File.Exists(young), "A young file is gone.");
        Assert.True(File.Exists(CopyPath(Record)), "The file of a document is gone.");
        Assert.True(File.Exists(KeptPath(OtherRecord)), "The last good bytes of a gone document are gone.");
        Assert.True(File.Exists(Path.Combine(_copy, "index.json")), "The index is gone.");
    }

    [Fact]
    public void Open_AGoneDocumentWhoseLastGoodBytesAreMissing_NoLongerCountsInTheBudget()
    {
        Write(Record, "record");
        var copy = OpenAt(Limits(documents: 1));
        copy.Load();
        _clock.Advance(TimeSpan.FromDays(2));
        File.Delete(_share.DocumentPath(Record));
        copy.Load();
        File.Delete(KeptPath(Record));
        Write(OtherRecord, "new");

        var load = OpenAt(Limits(documents: 1)).Load();

        Assert.Equal(OtherRecord, Assert.Single(load.Changed).Key);
        Assert.Empty(load.Skipped);
    }

    /// <summary>A load keeps a gone document only for its last good bytes, so an entry without them would hold a place for good.</summary>
    [Fact]
    public void Open_AGoneEntryWithoutLastGoodBytes_IsLeftOut()
    {
        Write(Record, "record");
        Open().Load();
        ChangeIndex(index =>
        {
            // In the copy for a year before it went, so the eviction of brief leftovers does not take it.
            var entry = index["files"]![0]!.AsObject();
            entry["id"] = "b72e09";
            entry["taken"] = "2025-01-01T00:00:00+00:00";
            entry["goneAt"] = "2026-01-01T00:00:00+00:00";
            entry["copied"] = 0;
        });

        var load = Open(Limits(documents: 1)).Load();

        Assert.Equal(Record, Assert.Single(load.Changed).Key);
        Assert.Empty(load.Skipped);
    }

    [Fact]
    public void Open_WhenTheIndexCannotBeReadNow_FailsAndDeletesNothing()
    {
        Write(Record, "record");
        OpenAt().Load();
        File.WriteAllText(Path.Combine(_copy, "data", "a1b2c3.json"), "made up");
        _clock.Advance(TimeSpan.FromMinutes(11));

        using (new FileStream(Path.Combine(_copy, "index.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => OpenAt());
        }

        Assert.True(File.Exists(CopyPath(Record)), "The file of a document is gone.");
        Assert.True(File.Exists(Path.Combine(_copy, "data", "a1b2c3.json")), "A file that no entry names is gone.");
    }

    /// <summary>
    /// A file of the copy that another program holds at the start: the next load reads the document again from the share,
    /// and when the share no longer has it, its last good bytes stay (third security review of M2.7).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Documents_AFileOfTheLocalCopyThatCannotBeReadNow_IsReadAgain_AndKeepsItsLastGoodBytes(bool stillOnTheShare)
    {
        Write(Record, "record");
        Write(OtherRecord, "other");
        Open().Load();
        var copy = Open();
        IReadOnlyList<LoadedDocument> documents;
        using (new FileStream(CopyPath(Record), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            documents = copy.Documents();
        }

        Assert.Equal(OtherRecord, Assert.Single(documents).Key);
        if (!stillOnTheShare)
        {
            File.Delete(_share.DocumentPath(Record));
        }

        _files.Reads = 0;
        var load = copy.Load();

        if (stillOnTheShare)
        {
            Assert.Equal(Record, Assert.Single(load.Changed).Key);
            Assert.Equal(1, _files.Reads);
            _files.Reads = 0;
            Assert.Empty(copy.Load().Changed);
            Assert.Equal(0, _files.Reads);
        }
        else
        {
            Assert.Equal(Record, Assert.Single(load.Gone));
            Assert.True(File.Exists(KeptPath(Record)), "The last good bytes of the gone document are gone.");
        }
    }

    [Fact]
    public void Load_AFaultOfTheCodeInALaterFolder_KeepsWhatTheLoadTookBefore_ForTheNextStart()
    {
        Write(Settings, "settings");
        WriteRecords(3);
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.OpenRead) && path.EndsWith("r0001.json", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A made-up fault of the code.");
            }
        };

        Assert.Throws<InvalidOperationException>(() => Open().Load());
        _files.Before = null;

        Assert.Equal(Settings, Assert.Single(Open().Documents()).Key);
    }

    [Fact]
    public void Load_ADocumentThatWasNeverGoodAndGoes_LeavesNothingInTheBudget()
    {
        File.WriteAllText(_share.DocumentPath(Record), "{", Encoding.UTF8);
        var copy = OpenAt(Limits(documents: 1));
        copy.Load();
        _clock.Advance(TimeSpan.FromDays(2));
        File.Delete(_share.DocumentPath(Record));
        Assert.Equal(Record, Assert.Single(copy.Load().Gone));
        Write(OtherRecord, "new");

        Assert.Equal(OtherRecord, Assert.Single(copy.Load().Changed).Key);
    }

    /// <summary>A damaged document is fixed on the share: with the bytes it had, which the copy has, or with other bytes.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Load_ADamagedDocumentThatIsGoodAgain_IsGivenAndKept(bool sameBytes)
    {
        Write(Record, "good");
        var good = File.ReadAllBytes(_share.DocumentPath(Record));
        var copy = Open();
        copy.Load();
        File.WriteAllText(_share.DocumentPath(Record), "{", Encoding.UTF8);
        copy.Load();
        TestFolder.WriteAsKiler(_share.DocumentPath(Record), sameBytes ? good : DocumentFormat.Next(Record, 1, Content("fixed")));

        var load = copy.Load();

        Assert.Equal(sameBytes ? 1 : 2, Assert.Single(load.Changed).Read.Document?.Version);
        Assert.Equal(sameBytes ? 1 : 2, Assert.Single(Open().Documents()).Read.Document?.Version);
        Assert.Equal(File.ReadAllBytes(_share.DocumentPath(Record)), File.ReadAllBytes(CopyPath(Record)));
    }

    [Fact]
    public void Load_AFileWithATimeAfterTheYear9999_IsReported_AndIsNotGone_AndTheOthersLoad()
    {
        Write(Record, "record");
        var copy = Open();
        copy.Load();
        TestFolder.SetRawTimes(_share.DocumentPath(Record), TestFolder.FarFuture, TestFolder.FarFuture);
        var other = Path.Combine(_share.Root, DataFolder.Records, "desktop.ini");
        File.WriteAllText(other, "made up");
        TestFolder.SetRawTimes(other, TestFolder.FarFuture, TestFolder.FarFuture);
        Write(User, "new");

        var load = copy.Load();

        Assert.Equal(User, Assert.Single(load.Changed).Key);
        Assert.Empty(load.Gone);
        Assert.Equal(new SkippedEntry(DataFolder.Records, "8f3a1c.json", SkipReason.Unreadable), Assert.Single(load.Skipped));
        Assert.Contains(Open().Documents(), document => document.Key == Record);
    }

    [Fact]
    public void Open_AnIndexOfAnotherFormat_IsNotTaken()
    {
        Write(Record, "record");
        Open().Load();
        ChangeIndex(index => index["format"] = 1);

        Assert.Empty(Open().Documents());
    }

    [Fact]
    public void Open_DeletesTheOldTemporaryFilesThatAWriteOfTheCopyLeft()
    {
        Write(Record, "record");
        Open().Load();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(11));
        var old = Path.Combine(_copy, "index.0123abcd.tmp");
        var oldRecord = Path.Combine(Path.GetDirectoryName(CopyPath(Record))!, "8f3a1c.4567cdef.tmp");
        var young = Path.Combine(_copy, "index.89abcdef.tmp");
        var other = Path.Combine(_copy, "notes.tmp");
        foreach (var file in new[] { old, oldRecord, young, other })
        {
            File.WriteAllText(file, "made up");
        }

        File.SetLastWriteTimeUtc(young, time.GetUtcNow().UtcDateTime.AddMinutes(-1));

        DataCopy.Open(_share, _copy, time);

        Assert.False(File.Exists(old), "The old temporary file of the index is still there.");
        Assert.False(File.Exists(oldRecord), "The old temporary file of a record is still there.");
        Assert.True(File.Exists(young), "The young temporary file is gone.");
        Assert.True(File.Exists(other), "A file whose name the copy does not give is gone.");
    }

    [Fact]
    public void Open_TheLocalCopyOfAnotherDataFolder_IsNotTaken()
    {
        Write(Record, "record");
        Open().Load();
        var other = DataFolder.Create(Path.Combine(_test.Root, "other"), _files);
        TestFolder.WriteAsKiler(other.DocumentPath(Record), DocumentFormat.New(Record, Content("other")));

        var copy = DataCopy.Open(other, _copy, TimeProvider.System);

        Assert.Empty(copy.Documents());
        Assert.Equal("other", (string?)Assert.Single(copy.Load().Changed).Read.Document?.Content["name"]);
    }

    [Fact]
    public void Open_APrivateFolder_IsRefused()
    {
        var mine = _share.CreatePrivate("demo.user");

        Assert.Throws<ArgumentException>(() => DataCopy.Open(mine, _copy, TimeProvider.System));
    }

    [Fact]
    public void Open_AFolderThatDoesNotExist_FailsAndCreatesNothing()
    {
        var missing = Path.Combine(_test.Root, "missing");

        Assert.Throws<DirectoryNotFoundException>(() => DataCopy.Open(_share, missing, TimeProvider.System));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void Open_ARelativePath_IsRefused() => Assert.Throws<ArgumentException>(() => DataCopy.Open(_share, "copy", TimeProvider.System));

    private static JsonObject Content(string name) => new() { ["name"] = name };

    private static string[] Keys(IEnumerable<DocumentKey> keys) => [.. keys.Select(key => key.ToString()).Order(StringComparer.Ordinal)];

    private static string[] Keys(IEnumerable<LoadedDocument> documents) => Keys(documents.Select(document => document.Key));

    /// <summary>The budget of the copy, with the default of each part that a test does not give.</summary>
    internal static CopyLimits Limits(
        long? bytes = null,
        int? documents = null,
        int? listed = null,
        int? reports = null,
        long? historyBytes = null,
        int? historySlots = null,
        int? historyListings = null,
        long? historyReads = null) =>
        new(
            bytes ?? CopyLimits.Default.Bytes,
            documents ?? CopyLimits.Default.Documents,
            listed ?? CopyLimits.Default.Listed,
            reports ?? CopyLimits.Default.Reports,
            historyBytes ?? CopyLimits.Default.HistoryBytes,
            historySlots ?? CopyLimits.Default.HistorySlots,
            historyListings ?? CopyLimits.Default.HistoryListings,
            historyReads ?? CopyLimits.Default.HistoryReads);

    private DataCopy Open() => DataCopy.Open(_share, _copy, TimeProvider.System);

    private DataCopy Open(CopyLimits limits) => DataCopy.Open(_share, _copy, TimeProvider.System, limits);

    private string NewCopyFolder(string name) => Directory.CreateDirectory(Path.Combine(_test.Root, name)).FullName;

    /// <summary>Changes the index of the copy as JSON, as a file that is not what a load wrote.</summary>
    private void ChangeIndex(Action<JsonNode> change)
    {
        var path = Path.Combine(_copy, "index.json");
        var index = JsonNode.Parse(File.ReadAllBytes(path))!;
        change(index);
        File.WriteAllText(path, index.ToJsonString());
    }

    /// <summary>The requests of a load for the folders of the share: their holds, attributes and listings.</summary>
    private int RequestsForFolders(DataCopy copy)
    {
        var requests = 0;
        _files.Before = (step, _) =>
        {
            if (step is nameof(IFileSystem.HoldFolder) or nameof(IFileSystem.Attributes) or nameof(IFileSystem.List))
            {
                Interlocked.Increment(ref requests);
            }
        };
        copy.Load();
        _files.Before = null;
        return requests;
    }

    /// <summary>A document file on the share, as a write of Kiler leaves it, at version 1.</summary>
    private void Write(DocumentKey key, string name) => TestFolder.WriteAsKiler(_share.DocumentPath(key), DocumentFormat.New(key, Content(name)));

    /// <summary>The bytes of the record <c>r</c> and the number in four digits, of about a kilobyte, the same size for every number.</summary>
    private static byte[] RecordBytes(int number)
    {
        var key = DocumentKey.InFolder(DataFolder.Records, $"r{number:D4}");
        return DocumentFormat.New(key, new JsonObject { ["name"] = $"Демо запис {number:D4}", ["notes"] = new string('x', 1000) });
    }

    /// <summary>The size of a record that <see cref="WriteRecord"/> writes.</summary>
    private static long RecordSize => RecordBytes(0).Length;

    /// <summary>Records <c>r0000</c> and on, of about a kilobyte each, as design 5.3 counts them.</summary>
    private void WriteRecords(int count)
    {
        for (var i = 0; i < count; i++)
        {
            WriteRecord(i);
        }
    }

    private void WriteRecord(int number) =>
        TestFolder.WriteAsKiler(_share.DocumentPath(DocumentKey.InFolder(DataFolder.Records, $"r{number:D4}")), RecordBytes(number));

    private void DeleteRecords()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(_share.Root, DataFolder.Records)))
        {
            File.Delete(file);
        }
    }

    /// <summary>The names of the files of the records in the copy, those kept apart included, in order.</summary>
    private string[] CopyRecords() =>
        [.. ((string[])[Path.GetDirectoryName(CopyPath(Record))!, Path.GetDirectoryName(KeptPath(Record))!])
            .Where(Directory.Exists)
            .SelectMany(Directory.GetFiles)
            .Select(path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal)];

    /// <summary>Opens the copy on <see cref="_clock"/>, with the default budget or another one.</summary>
    private DataCopy OpenAt(CopyLimits? limits = null) => DataCopy.Open(_share, _copy, _clock, limits ?? CopyLimits.Default);

    /// <summary>Where the local copy keeps the file of a document: a mirror of the share in <c>data\</c> (decided in M2.7).</summary>
    private string CopyPath(DocumentKey key) => Path.Combine(_copy, "data", key.Folder ?? "", key.Id + ".json");

    /// <summary>Where the local copy keeps the last good bytes of a document apart, in <c>kept\</c> (decided at the start of M2, M2.7a).</summary>
    private string KeptPath(DocumentKey key) => Path.Combine(_copy, "kept", key.Folder ?? "", key.Id + ".json");
}

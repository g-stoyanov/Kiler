using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Storage.Tests;

/// <summary>
/// The back-offs of the load (M2.8b, decided by the user): a document whose times change with the same bytes, and a history
/// folder or a slot that fails, are tried again 20 s after the first time, twice as long after each time after it, at most 5
/// minutes. A document read again with the bytes that the copy has is not given again, unless its owner changed.
/// </summary>
public sealed class LoadBackOffTests : IDisposable
{
    private static readonly DocumentKey Record = DocumentKey.InFolder(DataFolder.Records, "8f3a1c");
    private static readonly DocumentKey OtherRecord = DocumentKey.InFolder(DataFolder.Records, "b72e09");

    /// <summary>The times that <see cref="Touch(string, int)"/> sets, a minute later at each pass.</summary>
    private static readonly DateTime Touched = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly TestFolder _test = new();
    private readonly TestFileSystem _files = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly DataFolder _share;
    private readonly string _copy;

    public LoadBackOffTests()
    {
        _share = DataFolder.Create(Path.Combine(_test.Root, "data"), _files);
        _copy = Directory.CreateDirectory(Path.Combine(_test.Root, "copy")).FullName;
    }

    public void Dispose() => _test.Dispose();

    [Fact]
    public void Load_ADocumentTouchedOnEveryPassWithTheSameBytes_IsReadLessOften_AndEveryPassAgainOnceItStaysStill()
    {
        Create(Record, "record");
        Create(OtherRecord, "other");
        var copy = Open();
        copy.Load();
        List<int> read = [];
        for (var pass = 1; pass <= 70; pass++)
        {
            _clock.Advance(DataWatch.Interval);
            Touch(Record, pass);
            _files.Reads = 0;
            copy.Load();
            if (_files.Reads > 0)
            {
                read.Add(pass);
            }
        }

        // 20 s after the first read with the same bytes, then 40 s, 80 s, 160 s, and at most 5 minutes.
        Assert.Equal([1, 3, 7, 15, 31, 61], read);

        // It stays still: the times that wait are read once the wait ends, and from then on each change at the next pass.
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, Reads(copy));
        _clock.Advance(DataWatch.Interval);
        Assert.Equal(0, Reads(copy));
        _clock.Advance(DataWatch.Interval);
        Touch(Record, 100);
        Assert.Equal(1, Reads(copy));
        _clock.Advance(DataWatch.Interval);
        Touch(OtherRecord, 100);
        Assert.Equal(1, Reads(copy));
    }

    [Fact]
    public void Load_ADocumentThatWaits_IsReadAtOnceWhenItsSizeChanged_AndOnceTheWaitEndsWithOtherBytesOfTheSameSize()
    {
        Create(Record, "record");
        var copy = Open();
        copy.Load();
        _clock.Advance(DataWatch.Interval);
        Touch(Record, 1);
        Assert.Equal(1, Reads(copy));
        _clock.Advance(DataWatch.Interval);
        Replace(Record, 1, "a longer record");

        var load = copy.Load();

        Assert.Equal("a longer record", Name(Assert.Single(load.Changed)));
        _clock.Advance(DataWatch.Interval);
        Touch(Record, 2);
        Assert.Equal(1, Reads(copy));
        _clock.Advance(DataWatch.Interval);
        Replace(Record, 2, "a longer recorD");
        Assert.Empty(copy.Load().Changed);
        _clock.Advance(DataWatch.Interval);

        Assert.Equal("a longer recorD", Name(Assert.Single(copy.Load().Changed)));

        // Other bytes end the back-off: the next touch is read at the next pass.
        _clock.Advance(DataWatch.Interval);
        Touch(Record, 3);
        Assert.Equal(1, Reads(copy));
    }

    [Fact]
    public void Load_ADocumentThatWaits_IsReadAtOnceWhenTheClockIsPutBack()
    {
        Create(Record, "record");
        var copy = Open();
        copy.Load();
        _clock.Advance(DataWatch.Interval);
        Touch(Record, 1);
        Assert.Equal(1, Reads(copy));
        _clock.AdjustTime(_clock.GetUtcNow() - TimeSpan.FromMinutes(1));
        Touch(Record, 2);

        Assert.Equal(1, Reads(copy));
    }

    [Fact]
    public void Load_ADocumentThatWentAndCameBack_IsReadAtOnce_AlsoWhileItWaited()
    {
        Create(Record, "record");
        var copy = Open();
        copy.Load();
        _clock.Advance(DataWatch.Interval);
        Touch(Record, 1);
        Assert.Equal(1, Reads(copy));
        var bytes = File.ReadAllBytes(_share.DocumentPath(Record));
        File.Delete(_share.DocumentPath(Record));
        Assert.Equal(Record, Assert.Single(copy.Load().Gone));
        TestFolder.WriteAsKiler(_share.DocumentPath(Record), bytes);

        var load = copy.Load();

        Assert.Equal("record", Name(Assert.Single(load.Changed)));
        Assert.Equal(Record, Assert.Single(Open().Documents()).Key);
    }

    [Fact]
    public void Load_ADocumentThatWaitsAndMovesToTheTrash_IsFollowedAtOnce_AndItsBackOffIsLetGo()
    {
        Create(Record, "record");
        var copy = Open();
        copy.Load();
        _clock.Advance(DataWatch.Interval);
        Touch(Record, 1);
        Assert.Equal(1, Reads(copy));
        Assert.Equal(1, copy.TouchesKept);
        Assert.Equal(WriteOutcome.Deleted, _share.DeleteDocument(Record, 1).Outcome);

        var load = copy.Load();

        Assert.Equal(Record, Assert.Single(load.Gone));
        Assert.True(load.TrashChanged, "The move to the trash did not change the trash.");
        Assert.Equal(Record, Assert.Single(copy.Trash()).Key);
        Assert.Equal(0, copy.TouchesKept);
    }

    /// <summary>
    /// The upper layers have the bytes that the copy could not write, so the bytes that it has are given when they come back
    /// (code review of M2.8b).
    /// </summary>
    [Fact]
    public void Load_ADocumentWhoseBytesTheCopyCouldNotWrite_IsGivenAgain_WhenTheBytesThatTheCopyHasComeBack()
    {
        Create(Record, "record");
        var copy = Open();
        copy.Load();
        var good = File.ReadAllBytes(_share.DocumentPath(Record));
        Replace(Record, 1, "recorD");
        LoadResult load;
        using (new FileStream(CopyPath(Record), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            load = copy.Load();
        }

        Assert.Equal("recorD", Name(Assert.Single(load.Changed)));
        Assert.Equal(new SkippedEntry(DataFolder.Records, "8f3a1c.json", SkipReason.NotCopied), Assert.Single(load.Skipped));
        _clock.Advance(DataWatch.Interval);
        TestFolder.WriteAsKiler(_share.DocumentPath(Record), good);

        Assert.Equal("record", Name(Assert.Single(copy.Load().Changed)));
        Assert.Empty(copy.Load().Changed);
    }

    /// <summary>Only a document that the copy has good waits (decided by the user); one changed outside Kiler is read as before.</summary>
    [Fact]
    public void Load_ADocumentEditedInPlace_IsReadOnEveryPass_AlsoWhenItsTimesAloneChange()
    {
        Create(Record, "record");
        var copy = Open();
        copy.Load();
        var path = _share.DocumentPath(Record);
        TestFolder.EditInPlace(path, DocumentFormat.Next(Record, 1, Content("recorD")));
        _clock.Advance(DataWatch.Interval);
        Assert.Equal(1, Reads(copy));
        for (var pass = 1; pass <= 3; pass++)
        {
            _clock.Advance(DataWatch.Interval);
            var time = Touched.AddMinutes(pass);
            File.SetCreationTimeUtc(path, time);
            File.SetLastWriteTimeUtc(path, time.AddMinutes(1));
            _files.Reads = 0;

            var load = copy.Load();

            Assert.Equal(1, _files.Reads);
            Assert.Empty(load.Changed);
        }
    }

    [Fact]
    public void Load_ADocumentWhoseFileInTheCopyCouldNotBeRead_IsReadAtOnce_AlsoWhileItWaits_AndThatIsNoTouch()
    {
        Create(Record, "record");
        var copy = Open();
        copy.Load();
        _clock.Advance(DataWatch.Interval);
        Touch(Record, 1);
        Assert.Equal(1, Reads(copy));
        using (new FileStream(CopyPath(Record), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(copy.Documents());
        }

        _clock.Advance(DataWatch.Interval);
        Touch(Record, 2);
        _files.Reads = 0;

        var load = copy.Load();

        Assert.Equal(1, _files.Reads);
        Assert.Equal("record", Name(Assert.Single(load.Changed)));
        Assert.Equal(Record, Assert.Single(copy.Documents()).Key);
        _clock.Advance(DataWatch.Interval);
        Touch(Record, 3);
        Assert.Equal(1, Reads(copy));
    }

    [Fact]
    public void Load_ADocumentReadAgainWithTheSameBytes_IsNotGivenAgain_UnlessItsOwnerChanged()
    {
        Create(Record, "record");
        Create(OtherRecord, "in the trash");
        Assert.Equal(WriteOutcome.Deleted, _share.DeleteDocument(OtherRecord, 1).Outcome);
        var copy = Open();
        copy.Load();
        _clock.Advance(DataWatch.Interval);
        Touch(Record, 1);
        Touch(_share.TrashPath(OtherRecord), 1);
        _files.Reads = 0;

        var load = copy.Load();

        Assert.Equal(2, _files.Reads);
        Assert.Empty(load.Changed);
        Assert.False(load.TrashChanged, "A document of the trash read again with the same bytes changed the trash.");
        _clock.Advance(BackOff.First);
        Touch(Record, 2);
        _files.Owners.Set(_share.DocumentPath(Record), TestOwners.Other);

        var changed = Assert.Single(copy.Load().Changed);

        Assert.Equal(TestOwners.Other, changed.Read.Owner);
        Assert.Equal("record", Name(changed));
        Assert.Equal(TestOwners.Other, Assert.Single(Open().Documents()).Read.Owner);
    }

    /// <summary>An owner that cannot be read is never the same (M2.6).</summary>
    [Fact]
    public void Load_ADocumentReadAgainWithTheSameBytes_IsGivenAgain_WhenItsOwnerCannotBeRead()
    {
        Create(Record, "record");
        _files.Owners.Set(_share.DocumentPath(Record), null);
        var copy = Open();
        Assert.Null(Assert.Single(copy.Load().Changed).Read.Owner);
        _clock.Advance(DataWatch.Interval);
        Touch(Record, 1);

        var changed = Assert.Single(copy.Load().Changed);

        Assert.Null(changed.Read.Owner);
        Assert.Equal("record", Name(changed));
    }

    [Fact]
    public void Load_AfterAWriteOfThisKiler_ReadsTheDocumentBack_AlsoWhileItWaits_AndGivesItWithItsOwner()
    {
        Create(Record, "record");
        var copy = Open();
        copy.Load();
        _clock.Advance(DataWatch.Interval);
        Touch(Record, 1);
        Assert.Equal(1, Reads(copy));
        Assert.Equal(WriteOutcome.Written, copy.Replace(Record, 1, Content("recorD")).Outcome);
        _clock.Advance(DataWatch.Interval);
        _files.Reads = 0;

        var load = copy.Load();

        Assert.Equal(1, _files.Reads);
        Assert.Equal(TestOwners.Me, Assert.Single(load.Changed).Read.Owner);

        // The read back is no touch: the next one is read at the next pass.
        _clock.Advance(DataWatch.Interval);
        Touch(Record, 2);
        Assert.Equal(1, Reads(copy));
    }

    /// <summary>The copy keeps what it had, with a stamp that makes the next load read the share's file (M2.7a).</summary>
    [Fact]
    public void Load_AWriteOfThisKilerThatTheCopyCouldNotTake_IsReadAtTheNextLoad_AlsoWhileTheDocumentWaits()
    {
        Create(Record, "record");
        var copy = Open();
        copy.Load();
        _clock.Advance(DataWatch.Interval);
        Touch(Record, 1);
        Assert.Equal(1, Reads(copy));
        using (new FileStream(CopyPath(Record), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(WriteOutcome.Written, copy.Replace(Record, 1, Content("recorD")).Outcome);
        }

        _clock.Advance(DataWatch.Interval);

        Assert.Equal("recorD", Name(Assert.Single(copy.Load().Changed)));
    }

    [Fact]
    public void Load_AHistoryFolderThatCannotBeListed_IsListedAgainLessOften_AndEachLoadReportsWhatItsLastTryLeft()
    {
        Create(Record, "first");
        var copy = Open();
        copy.Load();
        Replace(Record, 1, "second");
        Replace(Record, 2, "third");
        var history = Path.GetDirectoryName(_share.HistoryPath(Record, 1))!;
        var failing = true;
        var lists = 0;
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.List) && path == history)
            {
                lists++;
                if (failing)
                {
                    throw new IOException("A made-up failure of the listing.");
                }
            }
        };
        var unreadable = new SkippedEntry(@"history\records\8f3a1c", null, SkipReason.Unreadable);
        Assert.Equal(unreadable, Assert.Single(copy.Load().Skipped));
        List<int> listed = [];
        for (var pass = 1; pass <= 40; pass++)
        {
            _clock.Advance(DataWatch.Interval);
            lists = 0;
            Assert.Equal(unreadable, Assert.Single(copy.Load().Skipped));
            if (lists > 0)
            {
                listed.Add(pass);
            }
        }

        // 20 s after the first failure, then 40 s, 80 s, 160 s, and at most 5 minutes.
        Assert.Equal([2, 6, 14, 30], listed);
        failing = false;
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty(copy.Load().Skipped);
        Assert.Equal(["v1.json", "v2.json"], SlotCopies(Record));
        lists = 0;
        _clock.Advance(DataWatch.Interval);
        Assert.Empty(copy.Load().Skipped);
        Assert.Equal(0, lists);
    }

    [Fact]
    public void Load_ASlotThatCannotBeRead_IsReadAgainAfterItsWait_AndEachLoadReportsItMeanwhile()
    {
        Create(Record, "v1");
        var copy = Open();
        copy.Load();
        Replace(Record, 1, "v2");
        Replace(Record, 2, "v3");
        var slot = _share.HistoryPath(Record, 2);
        var unreadable = new SkippedEntry(@"history\records\8f3a1c", "v2.json", SkipReason.Unreadable);
        TestFolder.DenyRead(slot);
        try
        {
            Assert.Equal(unreadable, Assert.Single(copy.Load().Skipped));
            _files.Reads = 0;
            _clock.Advance(DataWatch.Interval);
            Assert.Equal(unreadable, Assert.Single(copy.Load().Skipped));
            Assert.Equal(0, _files.Reads);
        }
        finally
        {
            TestFolder.AllowRead(slot);
        }

        Assert.Equal(unreadable, Assert.Single(copy.Load().Skipped));
        _clock.Advance(DataWatch.Interval);

        Assert.Empty(copy.Load().Skipped);
        Assert.Equal(["v1.json", "v2.json"], SlotCopies(Record));
    }

    [Fact]
    public void Load_TheReportsOfTheHistoryFoldersThatWait_AreKeptOnlyUpToTheLimitOfAReport_AndTheRestCounted()
    {
        DocumentKey[] records = [Record, OtherRecord, DocumentKey.InFolder(DataFolder.Records, "c3d4e5")];
        foreach (var key in records)
        {
            Create(key, "first");
        }

        var copy = Open(DataCopyTests.Limits(reports: 2));
        copy.Load();
        foreach (var key in records)
        {
            Replace(key, 1, "second");
        }

        var history = Path.Combine(_share.Root, DataFolder.History) + Path.DirectorySeparatorChar;
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.List) && path.StartsWith(history, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("A made-up failure of the listing.");
            }
        };
        var tried = copy.Load();
        Assert.Equal(2, tried.Skipped.Count);
        Assert.Equal(1, tried.MoreSkipped);
        Assert.Equal(2, copy.HistorySkipsKept);
        _clock.Advance(DataWatch.Interval);

        var waits = copy.Load();

        Assert.Equal(tried.Skipped, waits.Skipped);
        Assert.Equal(1, waits.MoreSkipped);
    }

    private static JsonObject Content(string name) => new() { ["name"] = name };

    private static string? Name(LoadedDocument document) => (string?)document.Read.Document?.Content["name"];

    /// <summary>Opens the copy on <see cref="_clock"/>, with the default budget or another one.</summary>
    private DataCopy Open(CopyLimits? limits = null) => DataCopy.Open(_share, _copy, _clock, limits ?? CopyLimits.Default);

    /// <summary>A document on the share, as a write of another Kiler makes it.</summary>
    private void Create(DocumentKey key, string name) => Assert.Equal(WriteOutcome.Written, _share.CreateDocument(key, Content(name)).Outcome);

    /// <summary>A new version of a document on the share, as a write of another Kiler makes it, with the old one in its history slot.</summary>
    private void Replace(DocumentKey key, int version, string name) => Assert.Equal(WriteOutcome.Written, _share.ReplaceDocument(key, version, Content(name)).Outcome);

    /// <summary>Sets new times on the file of a document, with its bytes as they are, as a write of Kiler leaves them (M2.3).</summary>
    private void Touch(DocumentKey key, int pass) => Touch(_share.DocumentPath(key), pass);

    private static void Touch(string path, int pass)
    {
        var time = Touched.AddMinutes(pass);
        File.SetCreationTimeUtc(path, time);
        File.SetLastWriteTimeUtc(path, time);
    }

    /// <summary>The files of the share that a load reads.</summary>
    private int Reads(DataCopy copy)
    {
        _files.Reads = 0;
        copy.Load();
        return _files.Reads;
    }

    /// <summary>The file of a document in the copy: a mirror of the share in <c>data\</c> (decided in M2.7).</summary>
    private string CopyPath(DocumentKey key) => Path.Combine(_copy, "data", key.Folder ?? "", key.Id + ".json");

    /// <summary>The history slots that the copy has of a document, by name.</summary>
    private string[] SlotCopies(DocumentKey key)
    {
        var folder = Path.Combine(_copy, "data", "history", key.Folder ?? "", key.Id);
        return Directory.Exists(folder) ? [.. Directory.GetFiles(folder).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)] : [];
    }
}

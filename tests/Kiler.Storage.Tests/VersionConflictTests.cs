using System.Text.Json.Nodes;

namespace Kiler.Storage.Tests;

/// <summary>
/// Versions and conflicts (M2.4, design 5.5): a write names the version it changes, and its history slot is the lock, so of
/// two writes of one version only one gets through. A slot that a crash left with the bytes of the document is taken over
/// once it is <see cref="DataFolder.SlotAge"/> older than the write, on the clock of the file server.
/// </summary>
public sealed class VersionConflictTests : IDisposable
{
    private static readonly DocumentKey Record = DocumentKey.InFolder(DataFolder.Records, "8f3a1c");
    private static readonly DateTime Old = new(2020, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>The temporary file of the document, as the steps get its path.</summary>
    private const string DocumentTemporary = @"\records\8f3a1c.";

    /// <summary>The temporary file of the history slot of version 1.</summary>
    private const string SlotTemporary = @"\8f3a1c\v1.";

    /// <summary>The history slot of version 1.</summary>
    private const string Slot = @"\8f3a1c\v1.json";

    private const int Rounds = 100;

    private readonly TestFolder _test = new();
    private readonly string _data;
    private readonly string _records;
    private readonly string _slots;

    public VersionConflictTests()
    {
        _data = Path.Combine(_test.Root, "data");
        _records = Path.Combine(_data, "records");
        _slots = Path.Combine(_data, "history", "records", "8f3a1c");
    }

    /// <summary>The step, a part of its path, which call of the two, and the version on disk after a crash there.</summary>
    public static TheoryData<string, string, int, int> TakeOverSteps => new()
    {
        { nameof(IFileSystem.Attributes), Slot, 1, 1 },
        { nameof(IFileSystem.HoldFile), Slot, 1, 1 },
        { nameof(IFileSystem.DeleteHeldFile), Slot, 1, 1 },
        { nameof(IFileSystem.MoveFile), SlotTemporary, 2, 1 },
        { nameof(IFileSystem.MoveFile), DocumentTemporary, 1, 1 },
        { nameof(IFileSystem.SetCreationToWriteTime), DocumentTemporary, 2, 2 },
    };

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public void Dispose() => _test.Dispose();

    [Fact]
    public void ReplaceDocument_AfterAConflict_KeepingOnesOwnContentWritesOverTheVersionOnDisk()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        data.CreateDocument(Record, Content("first"));
        data.ReplaceDocument(Record, 1, Content("theirs"));
        var theirs = File.ReadAllBytes(data.DocumentPath(Record));

        var conflict = data.ReplaceDocument(Record, 1, Content("mine"));
        var kept = data.ReplaceDocument(Record, conflict.OnDisk!.Version, Content("mine"));

        Assert.Equal((WriteOutcome.Conflict, 2), (conflict.Outcome, conflict.OnDisk.Version));
        Assert.Equal("theirs", (string?)conflict.OnDisk.Content["name"]);
        Assert.Equal((WriteOutcome.Written, 3), (kept.Outcome, kept.Version));
        Assert.Equal("mine", (string?)data.Read(Record)?.Document?.Content["name"]);
        Assert.Equal(theirs, File.ReadAllBytes(data.HistoryPath(Record, 2)));
    }

    /// <summary>Anyone on the share can set the last version; keeping one's own content over it is refused (M2.2).</summary>
    [Fact]
    public void ReplaceDocument_KeepingOnesOwnContentOverTheLastVersion_GivesNoNextVersionAndLeavesNoSlot()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        File.WriteAllBytes(data.DocumentPath(Record), DocumentFormat.Next(Record, int.MaxValue - 1, Content("theirs")));

        var conflict = data.ReplaceDocument(Record, 1, Content("mine"));
        var kept = data.ReplaceDocument(Record, conflict.OnDisk!.Version, Content("mine"));

        Assert.Equal((WriteOutcome.Conflict, int.MaxValue), (conflict.Outcome, conflict.OnDisk.Version));
        Assert.Equal(WriteOutcome.NoNextVersion, kept.Outcome);
        Assert.Equal("theirs", (string?)data.Read(Record)?.Document?.Content["name"]);
        Assert.Empty(Names(Path.Combine(_data, "history")));
    }

    /// <summary>Two machines save the same version at the same moment: the slot lets only one through, in every round.</summary>
    [Fact]
    public async Task ReplaceDocument_TwoWritersOfOneVersionAtOnce_OneWinsEveryRoundAndEachVersionHasOneSlot()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        data.CreateDocument(Record, Content("first"));
        DataFolder[] writers = [DataFolder.Open(_data, new TestOwners()), DataFolder.Open(_data, new TestOwners())];

        for (var version = 1; version <= Rounds; version++)
        {
            var round = version;
            var results = await AtOnce(writers.Select((writer, i) => (Func<WriteResult>)(() => writer.ReplaceDocument(Record, round, Content($"{i} {round}")))));

            AssertOneWins(data, results, round);
        }

        AssertOneSlotPerVersion(data, Rounds);
    }

    [Fact]
    public void ReplaceDocument_AYoungLeftoverSlotWithTheBytesOfTheDocument_GivesConflictAndChangesNothing()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        var slot = MakeSlot(data, Record, before);

        var result = data.ReplaceDocument(Record, 1, Content("second"));

        Assert.Equal((WriteOutcome.Conflict, 1), (result.Outcome, result.OnDisk?.Version));
        Assert.Equal(before, File.ReadAllBytes(data.DocumentPath(Record)));
        Assert.Equal(before, File.ReadAllBytes(slot));
        Assert.Equal(["v1.json"], Names(_slots));
        Assert.Equal(["8f3a1c.json"], Names(_records));
    }

    [Theory]
    [InlineData(false, DataFolder.Records, "8f3a1c")]
    [InlineData(false, null, DataFolder.Settings)]
    [InlineData(true, DataFolder.Records, "8f3a1c")]
    [InlineData(true, null, DataFolder.Key)]
    public void ReplaceDocument_AnOldLeftoverSlotWithTheBytesOfTheDocument_IsTakenOver(bool inPrivate, string? folder, string id)
    {
        var shared = DataFolder.Create(_data, new TestOwners());
        var data = inPrivate ? shared.CreatePrivate("demo.user") : shared;
        var key = folder is null ? DocumentKey.AtRoot(id) : DocumentKey.InFolder(folder, id);
        data.CreateDocument(key, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(key));
        var slot = MakeSlot(data, key, before, Old);

        var result = data.ReplaceDocument(key, 1, Content("second"));

        Assert.Equal((WriteOutcome.Written, 2), (result.Outcome, result.Version));
        Assert.Equal("second", (string?)data.Read(key)?.Document?.Content["name"]);
        Assert.Equal(before, File.ReadAllBytes(slot));
        Assert.True(File.GetLastWriteTimeUtc(slot) > Old, "The leftover is still the slot.");
        Assert.Equal(["v1.json"], Names(Path.GetDirectoryName(slot)!));
    }

    /// <summary>
    /// The age of a slot is measured against the write time of the new temporary file of the write, which the file server
    /// gives, as the slot's own: a difference between the clocks of the machines does not count. The file server here is an
    /// hour ahead of this machine.
    /// </summary>
    [Theory]
    [InlineData(-1, WriteOutcome.Conflict)]
    [InlineData(0, WriteOutcome.Written)]
    public void ReplaceDocument_ALeftoverSlot_IsTakenOverFromItsAgeOnTheClockOfTheFileServer(int secondsOverTheAge, WriteOutcome outcome)
    {
        var files = new TestFileSystem { WriteTakes = TimeSpan.FromHours(1) };
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var slot = MakeSlot(data, Record, File.ReadAllBytes(data.DocumentPath(Record)));
        files.Before = (step, at) =>
        {
            // The new temporary file of the document has its times by now.
            if (step == nameof(IFileSystem.CreateFile) && at.Contains(SlotTemporary, StringComparison.Ordinal))
            {
                var now = File.GetLastWriteTimeUtc(Directory.GetFiles(_records, "8f3a1c.*.tmp").Single());
                File.SetLastWriteTimeUtc(slot, now - DataFolder.SlotAge - TimeSpan.FromSeconds(secondsOverTheAge));
            }
        };

        Assert.Equal(outcome, data.ReplaceDocument(Record, 1, Content("second")).Outcome);
    }

    /// <summary>
    /// Only the version that the write replaces is taken over: whole, byte for byte, and within the size limit. An empty slot
    /// is the one exception since M2.9b: no write makes one, so it is the dead lock of a write back, which is taken over
    /// (<see cref="DataCopyWriteBackOverTests.AWriteOfTheVersionOfADeadLock_TakesItOver_OnceItIsOld"/>).
    /// </summary>
    [Theory]
    [InlineData("other content")]
    [InlineData("a byte more")]
    [InlineData("half")]
    [InlineData("over the limit")]
    public void ReplaceDocument_AnOldSlotWithOtherBytes_IsNeverTakenOver(string kind)
    {
        var data = DataFolder.Create(_data, new TestOwners());
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        byte[] other = kind switch
        {
            "other content" => DocumentFormat.New(Record, Content("another first")),
            "a byte more" => [.. before, (byte)' '],
            "half" => before[..(before.Length / 2)],
            _ => [.. before, .. new byte[DocumentFormat.MaxSize]],
        };
        var slot = MakeSlot(data, Record, other, Old);

        var result = data.ReplaceDocument(Record, 1, Content("second"));

        Assert.Equal(WriteOutcome.Conflict, result.Outcome);
        Assert.Equal(before, File.ReadAllBytes(data.DocumentPath(Record)));
        Assert.True(File.ReadAllBytes(slot).SequenceEqual(other), "The slot was changed.");
        Assert.Equal(["v1.json"], Names(_slots));
    }

    /// <summary>
    /// A write that stopped for a while after it read the document finds the slot of a write that has replaced it since: the
    /// slot has the bytes it read and is old, but it is history, and the document is newer.
    /// </summary>
    [Fact]
    public void ReplaceDocument_AWriteThatStoppedAfterItsRead_DoesNotTakeOverTheSlotOfAWriteThatReplacedTheDocument()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        var slot = data.HistoryPath(Record, 1);
        var stopped = false;
        files.Before = (step, at) =>
        {
            // Another machine writes while this write stops between its read and its temporary file, for minutes.
            if (step == nameof(IFileSystem.CreateFile) && at.Contains(DocumentTemporary, StringComparison.Ordinal) && !stopped)
            {
                stopped = true;
                Assert.Equal(WriteOutcome.Written, DataFolder.Open(_data, new TestOwners()).ReplaceDocument(Record, 1, Content("theirs")).Outcome);
                File.SetLastWriteTimeUtc(slot, Old);
            }
        };

        var result = data.ReplaceDocument(Record, 1, Content("mine"));

        Assert.Equal(WriteOutcome.Conflict, result.Outcome);
        var read = data.Read(Record)?.Document;
        Assert.Equal((2, "theirs"), (read?.Version, (string?)read?.Content["name"]));
        Assert.Equal(before, File.ReadAllBytes(slot));
        Assert.Equal(Old, File.GetLastWriteTimeUtc(slot));
        Assert.Equal(["v1.json"], Names(_slots));
    }

    /// <summary>
    /// A write keeps its slot open to write until it has replaced the document, so a write that stops after its slot for
    /// longer than the age keeps it: another write cannot hold it to take it over.
    /// </summary>
    [Fact]
    public void ReplaceDocument_AWriteThatStopsAfterItsSlot_KeepsItsSlotAsLongAsItRuns()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var slot = data.HistoryPath(Record, 1);
        WriteResult? other = null;
        files.Before = (step, at) =>
        {
            // Minutes go by before the rename over the document, and another machine saves the same version.
            if (step == nameof(IFileSystem.MoveFile) && at.Contains(DocumentTemporary, StringComparison.Ordinal) && other is null)
            {
                File.SetLastWriteTimeUtc(slot, Old);
                other = DataFolder.Open(_data, new TestOwners()).ReplaceDocument(Record, 1, Content("theirs"));
            }
        };

        var result = data.ReplaceDocument(Record, 1, Content("mine"));

        Assert.Equal(WriteOutcome.Conflict, other?.Outcome);
        Assert.Equal(WriteOutcome.Written, result.Outcome);
        Assert.Equal("mine", (string?)data.Read(Record)?.Document?.Content["name"]);
        Assert.Equal(["v1.json"], Names(_slots));
    }

    /// <summary>
    /// The slot is not held before the takeover holds it, and the temporary file of the write shares the delete, so someone
    /// can empty the held slot folder and make it a mount point in place (M2.3); the check of the held folders comes before
    /// the delete, not after it.
    /// </summary>
    [Fact]
    public void ReplaceDocument_WhenTheSlotFolderIsMadeALinkBeforeTheTakeover_DeletesNothingThroughIt()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        MakeSlot(data, Record, before, Old);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        var other = Path.Combine(elsewhere, "v1.json");
        File.WriteAllBytes(other, before);
        File.SetLastWriteTimeUtc(other, Old);
        TestFolder.SkipWithoutJunctions(_slots);
        files.Before = (step, _) =>
        {
            if (step == nameof(IFileSystem.HoldFile))
            {
                foreach (var file in Directory.GetFiles(_slots))
                {
                    File.Delete(file);
                }

                TestFolder.MakeMountPoint(_slots, elsewhere);
            }
        };

        Assert.Throws<IOException>(() => data.ReplaceDocument(Record, 1, Content("second")));

        Assert.True(File.Exists(other), "The takeover deleted a file through the link.");
        Assert.Equal(before, File.ReadAllBytes(data.DocumentPath(Record)));
    }

    /// <summary>A link at the name, to a file with the bytes of the document, is never held or followed.</summary>
    [Fact]
    public void ReplaceDocument_ASymbolicLinkAtTheSlotNameToAnOldCopy_IsNotTakenOver()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        var target = Path.Combine(Directory.CreateDirectory(Path.Combine(_test.Root, "outside")).FullName, "v1.json");
        File.WriteAllBytes(target, before);
        File.SetLastWriteTimeUtc(target, Old);
        var slot = data.HistoryPath(Record, 1);
        Directory.CreateDirectory(_slots);
        TestFolder.CreateFileLink(slot, target);

        Assert.Equal(WriteOutcome.Conflict, data.ReplaceDocument(Record, 1, Content("second")).Outcome);

        Assert.Equal(before, File.ReadAllBytes(target));
        Assert.True(File.GetAttributes(slot).HasFlag(FileAttributes.ReparsePoint), "The link is gone.");
        Assert.Equal(before, File.ReadAllBytes(data.DocumentPath(Record)));
    }

    /// <summary>The write of the slot gave up (busy) between the rename of this write and its look at the slot.</summary>
    [Fact]
    public void ReplaceDocument_ASlotThatGoesBeforeItIsHeld_GivesConflict_AndTheNextWriteWrites()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var slot = MakeSlot(data, Record, File.ReadAllBytes(data.DocumentPath(Record)));
        files.Before = (step, at) =>
        {
            if (step == nameof(IFileSystem.Attributes) && at.EndsWith(Slot, StringComparison.Ordinal))
            {
                File.Delete(slot);
            }
        };

        Assert.Equal(WriteOutcome.Conflict, data.ReplaceDocument(Record, 1, Content("second")).Outcome);

        files.Before = null;
        Assert.Equal(WriteOutcome.Written, data.ReplaceDocument(Record, 1, Content("second")).Outcome);
    }

    [Fact]
    public void ReplaceDocument_AnOldFolderAtTheSlotName_IsNotTakenOver()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        data.CreateDocument(Record, Content("first"));
        var slot = data.HistoryPath(Record, 1);
        Directory.CreateDirectory(slot);
        Directory.SetLastWriteTimeUtc(slot, Old);

        Assert.Equal(WriteOutcome.Conflict, data.ReplaceDocument(Record, 1, Content("second")).Outcome);

        Assert.True(Directory.Exists(slot));
        Assert.Equal(["v1.json"], Names(_slots));
    }

    [Fact]
    public void ReplaceDocument_AnOldReadOnlySlot_IsNotTakenOver()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        var slot = MakeSlot(data, Record, before, Old);
        File.SetAttributes(slot, FileAttributes.ReadOnly);
        try
        {
            Assert.Equal(WriteOutcome.Conflict, data.ReplaceDocument(Record, 1, Content("second")).Outcome);

            Assert.Equal(before, File.ReadAllBytes(slot));
            Assert.Equal(before, File.ReadAllBytes(data.DocumentPath(Record)));
            Assert.Equal(["v1.json"], Names(_slots));
        }
        finally
        {
            File.SetAttributes(slot, FileAttributes.Normal);
        }
    }

    /// <summary>Without delete sharing, as a program other than Kiler may open it, the slot cannot be held.</summary>
    [Fact]
    public void ReplaceDocument_AnOldSlotOpenWithoutDeleteSharing_GivesConflictUntilItIsClosed()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        var slot = MakeSlot(data, Record, before, Old);

        WriteResult whileOpen;
        using (new FileStream(slot, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            whileOpen = data.ReplaceDocument(Record, 1, Content("second"));
        }

        Assert.Equal(WriteOutcome.Conflict, whileOpen.Outcome);
        Assert.Equal(before, File.ReadAllBytes(data.DocumentPath(Record)));
        Assert.Equal(WriteOutcome.Written, data.ReplaceDocument(Record, 1, Content("second")).Outcome);
        Assert.Equal(before, File.ReadAllBytes(slot));
        Assert.Equal(["v1.json"], Names(_slots));
    }

    /// <summary>
    /// A reader that shares the delete, as Kiler reads a file, does not stop the takeover: the old slot leaves its name at
    /// once (POSIX semantics, measured on NTFS in M2.4), and the reader still reads it to its end.
    /// </summary>
    [Fact]
    public void ReplaceDocument_AnOldSlotThatSomeoneReads_IsTakenOver()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        var slot = MakeSlot(data, Record, before, Old);

        using (var reader = new FileStream(slot, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.Equal(WriteOutcome.Written, data.ReplaceDocument(Record, 1, Content("second")).Outcome);
            Assert.Equal(before, new BinaryReader(reader).ReadBytes(before.Length + 1));
        }

        Assert.Equal(before, File.ReadAllBytes(slot));
        Assert.True(File.GetLastWriteTimeUtc(slot) > Old, "The leftover is still the slot.");
        Assert.Equal(["v1.json"], Names(_slots));
    }

    /// <summary>
    /// The slot is held from its read to its delete, so no one can change it or put another at its name in between: a
    /// delete by the name would take the slot of another write that took it over at the same moment. Two writes at once
    /// (<see cref="ReplaceDocument_TwoWritersTakeOverOneOldSlotAtOnce_OneWinsEveryRound"/>) meet in that moment too seldom
    /// to show it.
    /// </summary>
    [Fact]
    public void ReplaceDocument_WhileItTakesOverASlot_NoOneCanChangeOrSwapIt()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var slot = MakeSlot(data, Record, File.ReadAllBytes(data.DocumentPath(Record)), Old);
        var done = new List<string>();
        files.Before = (step, at) =>
        {
            if (step is not (nameof(IFileSystem.DeleteHeldFile) or nameof(IFileSystem.DeleteFile)) || !at.EndsWith(Slot, StringComparison.Ordinal))
            {
                return;
            }

            if (Xunit.Record.Exception(() => new FileStream(slot, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose()) is null)
            {
                done.Add("opened to write");
            }

            if (Xunit.Record.Exception(() => File.Move(slot, slot + ".away")) is null)
            {
                done.Add("renamed");
                File.Move(slot + ".away", slot);
            }

            // As another write that takes over the slot at the same moment: it deletes the slot and makes its own.
            if (Xunit.Record.Exception(() => File.Delete(slot)) is null)
            {
                done.Add("swapped");
                File.WriteAllBytes(slot, DocumentFormat.New(Record, Content("another write")));
            }
        };

        Assert.Equal(WriteOutcome.Written, data.ReplaceDocument(Record, 1, Content("second")).Outcome);

        Assert.Empty(done);
    }

    /// <summary>A crash left the slot long ago, and two machines save at the same moment: only one takes it over.</summary>
    [Fact]
    public async Task ReplaceDocument_TwoWritersTakeOverOneOldSlotAtOnce_OneWinsEveryRound()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        data.CreateDocument(Record, Content("first"));
        DataFolder[] writers = [DataFolder.Open(_data, new TestOwners()), DataFolder.Open(_data, new TestOwners())];

        for (var version = 1; version <= Rounds; version++)
        {
            var round = version;
            MakeSlot(data, Record, File.ReadAllBytes(data.DocumentPath(Record)), Old, round);
            var results = await AtOnce(writers.Select((writer, i) => (Func<WriteResult>)(() => writer.ReplaceDocument(Record, round, Content($"{i} {round}")))));

            AssertOneWins(data, results, round);
        }

        AssertOneSlotPerVersion(data, Rounds);
    }

    [Theory]
    [MemberData(nameof(TakeOverSteps))]
    public void ReplaceDocument_ACrashAtAnyStepOfATakeOver_LeavesTheDocumentWholeAndTheNextWriteWrites(string operation, string path, int occurrence, int version)
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var old = File.ReadAllBytes(data.DocumentPath(Record));
        MakeSlot(data, Record, old, Old);
        var calls = 0;
        files.CrashAt = (step, at) => step == operation && at.Contains(path, StringComparison.Ordinal) && ++calls == occurrence;

        Assert.ThrowsAny<IOException>(() => data.ReplaceDocument(Record, 1, Content("second")));

        Assert.True(files.Crashed, "The write did not reach the step.");
        var next = DataFolder.Open(_data, new TestOwners());
        Assert.Equal(version == 1 ? old : DocumentFormat.Next(Record, 1, Content("second")), File.ReadAllBytes(next.DocumentPath(Record)));
        var slot = next.HistoryPath(Record, version);
        Assert.True(!File.Exists(slot) || File.ReadAllBytes(slot).SequenceEqual(old), "The slot is not the old version.");

        // A slot that this write made before the crash is another write under way, until it is old.
        if (File.Exists(slot) && DateTime.UtcNow - File.GetLastWriteTimeUtc(slot) < DataFolder.SlotAge)
        {
            Assert.Equal(WriteOutcome.Conflict, next.ReplaceDocument(Record, version, Content("again")).Outcome);
            File.SetLastWriteTimeUtc(slot, Old);
        }

        Assert.Equal(WriteOutcome.Written, next.ReplaceDocument(Record, version, Content("again")).Outcome);
    }

    private static JsonObject Content(string name) => new() { ["template"] = "db", ["name"] = name };

    /// <summary>The names in a folder, files and folders, in order.</summary>
    private static string[] Names(string folder) =>
        [.. Directory.GetFileSystemEntries(folder).Select(entry => Path.GetFileName(entry)).Order(StringComparer.Ordinal)];

    /// <summary>Runs the writes on threads of their own, released together, as writes from other machines.</summary>
    private static async Task<WriteResult[]> AtOnce(IEnumerable<Func<WriteResult>> writes)
    {
        var all = writes.ToArray();
        using var start = new Barrier(all.Length);
        return await Task.WhenAll(all.Select(write => Task.Factory.StartNew(
            () =>
            {
                start.SignalAndWait(Cancel);
                return write();
            },
            Cancel,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)));
    }

    /// <summary>
    /// A slot as a write or a crash leaves it: the bytes under the name of the slot of <paramref name="version"/>, written now
    /// or at <paramref name="written"/>.
    /// </summary>
    private static string MakeSlot(DataFolder data, DocumentKey key, byte[] bytes, DateTime? written = null, int version = 1)
    {
        var slot = data.HistoryPath(key, version);
        Directory.CreateDirectory(Path.GetDirectoryName(slot)!);
        File.WriteAllBytes(slot, bytes);
        if (written is { } time)
        {
            File.SetLastWriteTimeUtc(slot, time);
        }

        return slot;
    }

    /// <summary>Exactly one of the writes of <paramref name="version"/> was written, and the document is its content.</summary>
    private static void AssertOneWins(DataFolder data, WriteResult[] results, int version)
    {
        Assert.Equal([WriteOutcome.Written, WriteOutcome.Conflict], results.Select(result => result.Outcome).Order());
        var winner = Array.FindIndex(results, result => result.Outcome == WriteOutcome.Written);
        var read = data.Read(Record)?.Document;
        Assert.Equal(version + 1, read?.Version);
        Assert.Equal($"{winner} {version}", (string?)read?.Content["name"]);
    }

    /// <summary>
    /// The slots <c>v1.json</c> to <c>v<paramref name="versions"/>.json</c>, each with its version, and nothing else: no
    /// temporary file of a write that lost is left.
    /// </summary>
    private void AssertOneSlotPerVersion(DataFolder data, int versions)
    {
        Assert.Equal(Enumerable.Range(1, versions).Select(version => $"v{version}.json").Order(StringComparer.Ordinal), Names(_slots));
        for (var version = 1; version <= versions; version++)
        {
            Assert.Equal(version, DocumentFormat.Read(Record, File.ReadAllBytes(data.HistoryPath(Record, version))).Document?.Version);
        }

        Assert.Equal(["8f3a1c.json"], Names(_records));
    }
}

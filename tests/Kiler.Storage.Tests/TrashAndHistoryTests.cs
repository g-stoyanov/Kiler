using System.Text.Json.Nodes;

namespace Kiler.Storage.Tests;

/// <summary>
/// The trash and the history (M2.5, design 5.6). A delete names the version it deletes, puts it into its history slot as a
/// write does (M2.4), and moves the document to <c>trash\</c>. A restore moves it back without replacing and lets go of the
/// slot that the delete made. The versions of a document are its history slots and its current version, and an old version
/// comes back as a new write of its content.
/// </summary>
public sealed class TrashAndHistoryTests : IDisposable
{
    private static readonly DocumentKey Record = DocumentKey.InFolder(DataFolder.Records, "8f3a1c");
    private static readonly DateTime Old = new(2020, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>The document, as the steps get its path.</summary>
    private const string Document = @"\data\records\8f3a1c.json";

    /// <summary>Its copy in the trash.</summary>
    private const string InTrash = @"\trash\records\8f3a1c.json";

    /// <summary>The history slot of version 1.</summary>
    private const string Slot = @"\8f3a1c\v1.json";

    /// <summary>The temporary file of that slot.</summary>
    private const string SlotTemporary = @"\8f3a1c\v1.";

    private const int Rounds = 50;

    private readonly TestFolder _test = new();
    private readonly string _data;

    public TrashAndHistoryTests() => _data = Path.Combine(_test.Root, "data");

    /// <summary>In a private folder or not, the document folder (none for a document at the root), and the id.</summary>
    public static TheoryData<bool, string?, string> Places => new()
    {
        { false, DataFolder.Records, "8f3a1c" },
        { false, null, DataFolder.Settings },
        { true, DataFolder.Records, "8f3a1c" },
        { true, null, DataFolder.Key },
    };

    /// <summary>The steps of a delete, each with a part of its path.</summary>
    public static TheoryData<string, string> DeleteSteps => new()
    {
        { nameof(IFileSystem.HoldFolder), @"\data\records" },
        { nameof(IFileSystem.OpenRead), Document },
        { nameof(IFileSystem.HoldOrCreateFolder), @"\history\records\8f3a1c" },
        { nameof(IFileSystem.HoldOrCreateFolder), @"\trash\records" },
        { nameof(IFileSystem.CreateFile), SlotTemporary },
        { nameof(IFileSystem.SetCreationToWriteTime), SlotTemporary },
        { nameof(IFileSystem.MoveFile), SlotTemporary },
        { nameof(IFileSystem.CreateMarker), @"\data\records\8f3a1c." },
        { nameof(IFileSystem.CreateMarker), @"\trash\records\8f3a1c." },
        { nameof(IFileSystem.OpenTimes), Document },
        { nameof(IFileSystem.MoveFile), Document },
        { nameof(IFileSystem.OpenTimes), InTrash },
        { nameof(IFileSystem.KeepCreationTime), Document },
    };

    /// <summary>
    /// The steps of a restore from the trash, each with a part of its path, whether the document is back after a crash there,
    /// and whether the restore throws: a failure in the delete of the slot, after the document is back, does not.
    /// </summary>
    public static TheoryData<string, string, bool, bool> RestoreSteps => new()
    {
        { nameof(IFileSystem.HoldFolder), @"\trash\records", false, true },
        { nameof(IFileSystem.OpenTimes), InTrash, false, true },
        { nameof(IFileSystem.OpenRead), InTrash, false, true },
        { nameof(IFileSystem.CreateMarker), @"\trash\records\8f3a1c.", false, true },
        { nameof(IFileSystem.CreateMarker), @"\data\records\8f3a1c.", false, true },
        { nameof(IFileSystem.MoveFile), InTrash, false, true },
        { nameof(IFileSystem.OpenTimes), Document, true, true },
        { nameof(IFileSystem.KeepCreationTime), InTrash, true, true },
        { nameof(IFileSystem.HoldFolder), @"\history\records\8f3a1c", true, false },
        { nameof(IFileSystem.HoldFile), Slot, true, false },
        { nameof(IFileSystem.DeleteHeldFile), Slot, true, false },
    };

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public void Dispose() => _test.Dispose();

    [Theory]
    [MemberData(nameof(Places))]
    public void DeleteDocument_MovesTheFileToTheTrash_AndKeepsTheDeletedVersionInHistory(bool inPrivate, string? folder, string id)
    {
        var data = Root(inPrivate);
        var key = Key(folder, id);
        data.CreateDocument(key, Content("first"));
        data.ReplaceDocument(key, 1, Content("second"));
        var first = File.ReadAllBytes(data.HistoryPath(key, 1));
        var second = File.ReadAllBytes(data.DocumentPath(key));
        var stamp = StampOf(data.DocumentPath(key));

        var result = data.DeleteDocument(key, 2);

        Assert.Equal((WriteOutcome.Deleted, 2), (result.Outcome, result.Version));
        Assert.Null(data.Read(key));
        Assert.Equal(second, File.ReadAllBytes(data.TrashPath(key)));
        Assert.Equal(stamp, StampOf(data.TrashPath(key)));
        Assert.Equal(first, File.ReadAllBytes(data.HistoryPath(key, 1)));
        Assert.Equal(second, File.ReadAllBytes(data.HistoryPath(key, 2)));
        Assert.Equal(["v1.json", "v2.json"], Names(SlotFolder(data, key)));
    }

    [Theory]
    [MemberData(nameof(Places))]
    public void DeleteDocument_WithAnOldVersion_GivesConflictWithTheDocumentOnDisk_AndChangesNothing(bool inPrivate, string? folder, string id)
    {
        var data = Root(inPrivate);
        var key = Key(folder, id);
        data.CreateDocument(key, Content("first"));
        data.ReplaceDocument(key, 1, Content("second"));
        var before = File.ReadAllBytes(data.DocumentPath(key));

        var result = data.DeleteDocument(key, 1);

        Assert.Equal((WriteOutcome.Conflict, 2), (result.Outcome, result.OnDisk?.Version));
        Assert.Equal("second", (string?)result.OnDisk?.Content["name"]);
        Assert.Equal(before, File.ReadAllBytes(data.DocumentPath(key)));
        Assert.Equal(["v1.json"], Names(SlotFolder(data, key)));
        Assert.False(File.Exists(data.TrashPath(key)));
    }

    /// <summary>
    /// Anyone on the share can set the last version (M2.2). A delete has no next version to write, so the last version is
    /// deleted, listed and restored as any other.
    /// </summary>
    [Fact]
    public void DeleteAndRestore_AtTheLastVersion_Work()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        var last = DocumentFormat.Next(Record, int.MaxValue - 1, Content("last"));
        File.WriteAllBytes(data.DocumentPath(Record), last);

        Assert.Equal((WriteOutcome.Deleted, int.MaxValue), Outcome(data.DeleteDocument(Record, int.MaxValue)));
        Assert.Equal(last, File.ReadAllBytes(data.HistoryPath(Record, int.MaxValue)));
        Assert.Equal([(int.MaxValue, false)], data.Versions(Record, DateTimeOffset.UtcNow).Select(version => (version.Version, version.IsCurrent)));
        Assert.Equal(int.MaxValue, data.ReadVersion(Record, int.MaxValue)?.Document?.Version);

        Assert.Equal((WriteOutcome.Written, int.MaxValue), Outcome(data.RestoreFromTrash(Record)));
        Assert.Equal([(int.MaxValue, true)], data.Versions(Record, DateTimeOffset.UtcNow).Select(version => (version.Version, version.IsCurrent)));
        Assert.Equal(WriteOutcome.NoNextVersion, data.ReplaceDocument(Record, int.MaxValue, Content("after")).Outcome);
    }

    /// <summary>
    /// A file server may let anyone move a file in a folder they can write, but set its times only for its owner (code review
    /// of M2.5): the delete and the restore go on without putting the times back.
    /// </summary>
    [Fact]
    public void DeleteAndRestore_ADocumentWhoseTimesMayNotBeSet_Work()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        TestFolder.DenyWriteAttributes(data.DocumentPath(Record));

        Assert.Equal(WriteOutcome.Deleted, data.DeleteDocument(Record, 1).Outcome);
        Assert.Equal(WriteOutcome.Written, data.RestoreFromTrash(Record).Outcome);

        Assert.Equal(before, File.ReadAllBytes(data.DocumentPath(Record)));
    }

    /// <summary>The whole way over SMB and a network path, through the administrative share of this machine (as on CI).</summary>
    [Fact]
    public void TrashAndHistory_ThroughANetworkPath_Work()
    {
        var data = DataFolder.Create(Path.Combine(TestFolder.ThroughLoopbackShare(_test.Root), "data"), new TestOwners());
        data.CreateDocument(Record, Content("first"));
        data.ReplaceDocument(Record, 1, Content("second"));

        Assert.Equal(WriteOutcome.Deleted, data.DeleteDocument(Record, 2).Outcome);
        Assert.Equal((WriteOutcome.Written, 2), Outcome(data.RestoreFromTrash(Record)));
        Assert.Equal([1, 2], data.Versions(Record, DateTimeOffset.UtcNow).Select(version => version.Version));
        Assert.Equal("first", (string?)data.ReadVersion(Record, 1)?.Document?.Content["name"]);
        Assert.Equal(WriteOutcome.Written, data.ReplaceDocument(Record, 2, Content("third")).Outcome);
    }

    /// <summary>Another write or delete of the same version is under way, or one stopped there a moment ago (M2.4).</summary>
    [Fact]
    public void DeleteDocument_WhenItsHistorySlotExists_GivesConflict_AndChangesNothing()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        var slot = MakeSlot(data, Record, before);

        var result = data.DeleteDocument(Record, 1);

        Assert.Equal((WriteOutcome.Conflict, 1), (result.Outcome, result.OnDisk?.Version));
        Assert.Equal(before, File.ReadAllBytes(data.DocumentPath(Record)));
        Assert.Equal(before, File.ReadAllBytes(slot));
        Assert.False(File.Exists(data.TrashPath(Record)));
    }

    [Fact]
    public void DeleteDocument_AnOldLeftoverSlotWithTheBytesOfTheDocument_IsTakenOver()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        var slot = MakeSlot(data, Record, before, Old);

        Assert.Equal(WriteOutcome.Deleted, data.DeleteDocument(Record, 1).Outcome);

        Assert.Equal(before, File.ReadAllBytes(data.TrashPath(Record)));
        Assert.Equal(before, File.ReadAllBytes(slot));
        Assert.True(File.GetLastWriteTimeUtc(slot) > Old, "The leftover is still the slot.");
    }

    [Fact]
    public void DeleteDocument_AMissingOrADamagedDocument_DeletesNothing()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        Assert.Equal(WriteOutcome.Missing, data.DeleteDocument(Record, 1).Outcome);
        File.WriteAllText(data.DocumentPath(Record), """{"id": "8f3a1c", "version": """);

        var damaged = data.DeleteDocument(Record, 1);

        Assert.Equal((WriteOutcome.Damaged, DocumentDamage.NotJson), (damaged.Outcome, damaged.Damage));
        Assert.Equal("""{"id": "8f3a1c", "version": """, File.ReadAllText(data.DocumentPath(Record)));
        Assert.Empty(Names(Path.Combine(_data, "history")));
        Assert.Empty(Names(Path.Combine(_data, "trash")));
    }

    /// <summary>
    /// An older copy in the trash, as a document written back from the local copy can leave it (M2.9), has its version in
    /// history, so the delete replaces it.
    /// </summary>
    [Fact]
    public void DeleteDocument_ReplacesAnOlderCopyInTheTrash()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        data.CreateDocument(Record, Content("first"));
        var first = File.ReadAllBytes(data.DocumentPath(Record));
        data.ReplaceDocument(Record, 1, Content("second"));
        var second = File.ReadAllBytes(data.DocumentPath(Record));
        Directory.CreateDirectory(Path.GetDirectoryName(data.TrashPath(Record))!);
        File.WriteAllBytes(data.TrashPath(Record), first);

        Assert.Equal(WriteOutcome.Deleted, data.DeleteDocument(Record, 2).Outcome);

        Assert.Equal(second, File.ReadAllBytes(data.TrashPath(Record)));
        Assert.Equal(first, File.ReadAllBytes(data.HistoryPath(Record, 1)));
        Assert.Equal(second, File.ReadAllBytes(data.HistoryPath(Record, 2)));
    }

    /// <summary>
    /// A file that someone holds open without delete sharing cannot be moved, and a file that someone reads cannot be renamed
    /// over (M2.2): the document, or the older copy in the trash. The move is tried again, then the delete is busy, and its
    /// slot goes with it, so it can be tried again at once.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeleteDocument_WhileAFileThatItMovesIsHeldOpen_GivesBusy_AndTheNextDeleteDeletes(bool olderCopyInTheTrash)
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        var trash = data.TrashPath(Record);
        Directory.CreateDirectory(Path.GetDirectoryName(trash)!);
        File.WriteAllText(trash, "older");

        WriteResult result;
        using (olderCopyInTheTrash
            ? new FileStream(trash, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
            : new FileStream(data.DocumentPath(Record), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            result = data.DeleteDocument(Record, 1);
        }

        Assert.Equal(WriteOutcome.Busy, result.Outcome);
        Assert.Equal(DataFolder.RenameWaits, files.Waits);
        Assert.Equal(before, File.ReadAllBytes(data.DocumentPath(Record)));
        Assert.Equal("older", File.ReadAllText(trash));
        Assert.Empty(Names(SlotFolder(data, Record)));
        Assert.Equal(WriteOutcome.Deleted, data.DeleteDocument(Record, 1).Outcome);
        Assert.Equal(before, File.ReadAllBytes(trash));
    }

    /// <summary>
    /// Nothing holds the slot of a busy write during its waits, as the slot shares the delete: someone can delete it, and
    /// another write make its own at its name. The busy write lets go of its own slot through its handle and leaves the other
    /// one (M2.4).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_WhenItIsBusyAndItsSlotIsSwappedDuringTheWaits_DeletesOnlyItsOwnSlot(bool delete)
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var slot = data.HistoryPath(Record, 1);
        var other = DocumentFormat.New(Record, Content("another write"));
        var waits = 0;
        files.Before = (step, _) =>
        {
            if (step == nameof(IFileSystem.Wait) && waits++ == 0)
            {
                File.Delete(slot);
                File.WriteAllBytes(slot, other);
            }
        };

        WriteResult result;
        using (new FileStream(data.DocumentPath(Record), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            result = delete ? data.DeleteDocument(Record, 1) : data.ReplaceDocument(Record, 1, Content("second"));
        }

        Assert.Equal(WriteOutcome.Busy, result.Outcome);
        Assert.True(File.Exists(slot), "The busy write deleted the slot of another write.");
        Assert.Equal(other, File.ReadAllBytes(slot));
    }

    [Theory]
    [MemberData(nameof(DeleteSteps))]
    public void DeleteDocument_ACrashAtAnyStep_LeavesTheDocumentWholeAndTheNextDeleteDeletes(string operation, string path)
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        var calls = 0;
        files.CrashAt = (step, at) => step == operation && at.Contains(path, StringComparison.Ordinal) && ++calls == 1;

        Assert.ThrowsAny<IOException>(() => data.DeleteDocument(Record, 1));

        Assert.True(files.Crashed, "The delete did not reach the step.");
        var next = DataFolder.Open(_data, new TestOwners());
        var document = next.DocumentPath(Record);
        var trash = next.TrashPath(Record);
        Assert.True(File.Exists(document) != File.Exists(trash), "The document is in both places or in none.");
        Assert.Equal(before, File.ReadAllBytes(File.Exists(document) ? document : trash));
        var slot = next.HistoryPath(Record, 1);
        Assert.True(!File.Exists(slot) || File.ReadAllBytes(slot).SequenceEqual(before), "The slot is not the deleted version.");
        if (!File.Exists(document))
        {
            return;
        }

        // A slot that the delete made before the crash is another write under way, until it is old.
        if (File.Exists(slot))
        {
            Assert.Equal(WriteOutcome.Conflict, next.DeleteDocument(Record, 1).Outcome);
            File.SetLastWriteTimeUtc(slot, Old);
        }

        Assert.Equal(WriteOutcome.Deleted, next.DeleteDocument(Record, 1).Outcome);
        Assert.Equal(before, File.ReadAllBytes(trash));
    }

    /// <summary>
    /// Two machines change one version at the same moment, one to save it and one to delete it: the slot lets one of them
    /// through, in every round. The other gets a conflict, or finds the document gone.
    /// </summary>
    [Fact]
    public async Task ReplaceAndDeleteOfOneVersionAtOnce_OneWinsEveryRound()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        DataFolder[] writers = [DataFolder.Open(_data, new TestOwners()), DataFolder.Open(_data, new TestOwners())];

        for (var round = 1; round <= Rounds; round++)
        {
            var key = DocumentKey.InFolder(DataFolder.Records, $"r{round}");
            data.CreateDocument(key, Content("first"));
            var before = File.ReadAllBytes(data.DocumentPath(key));

            var results = await AtOnce(() => writers[0].ReplaceDocument(key, 1, Content("saved")), () => writers[1].DeleteDocument(key, 1));

            var (saved, deleted) = (results[0].Outcome, results[1].Outcome);
            if (saved == WriteOutcome.Written)
            {
                Assert.Equal(WriteOutcome.Conflict, deleted);
                Assert.Equal("saved", (string?)data.Read(key)?.Document?.Content["name"]);
                Assert.False(File.Exists(data.TrashPath(key)));
            }
            else
            {
                Assert.Equal(WriteOutcome.Deleted, deleted);
                Assert.Contains(saved, new[] { WriteOutcome.Conflict, WriteOutcome.Missing });
                Assert.Null(data.Read(key));
                Assert.Equal(before, File.ReadAllBytes(data.TrashPath(key)));
            }

            Assert.Equal(["v1.json"], Names(SlotFolder(data, key)));
            Assert.Equal(before, File.ReadAllBytes(data.HistoryPath(key, 1)));
        }
    }

    /// <summary>
    /// The trash folder of a document folder is made with its first delete. A symbolic link without a target, put at its name
    /// just before it is made, is not followed: the folder is made in one call that opens a link at its name as itself. The
    /// developer machine did not follow such a link with <c>Directory.CreateDirectory</c> either; the CI machine, where the
    /// tests run as an administrator, followed one with a create-new of a file (M2.3).
    /// </summary>
    [Fact]
    public void DeleteDocument_WhenASymbolicLinkIsPutWhereItsTrashFolderIsMade_IsRefusedAndMakesNothingWhereItPoints()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        var trashFolder = Path.Combine(_data, "trash", "records");
        var target = Path.Combine(_test.Root, "outside");
        var link = Path.Combine(_test.Root, "link");
        TestFolder.CreateFileLink(link, target);
        files.Before = (step, at) =>
        {
            if (step == nameof(IFileSystem.HoldOrCreateFolder) && at == trashFolder && !Path.Exists(at))
            {
                File.Move(link, trashFolder);
            }
        };

        Assert.Throws<IOException>(() => data.DeleteDocument(Record, 1));

        Assert.False(Path.Exists(target), "The folder was made where the link points.");
        Assert.Equal(before, File.ReadAllBytes(data.DocumentPath(Record)));
    }

    /// <summary>
    /// The trash folder is empty after a restore, and anyone who can write it can make it a mount point in place while it is
    /// held (M2.3). On NTFS the marker keeps it from being empty from before the slot on; where the marker keeps nothing, the
    /// check of the held folders before the move refuses the delete, so nothing goes through the link.
    /// </summary>
    [Fact]
    public void DeleteDocument_WhenTheEmptyTrashFolderIsMadeALinkInPlace_IsRefusedAndMovesNothingThere()
    {
        var files = new TestFileSystem { MarkersKeepNothing = true };
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        data.DeleteDocument(Record, 1);
        data.RestoreFromTrash(Record);
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        var trashFolder = Path.Combine(_data, "trash", "records");
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        TestFolder.SkipWithoutJunctions(trashFolder);
        files.Before = (step, _) =>
        {
            if (step == nameof(IFileSystem.OpenTimes))
            {
                TestFolder.MakeMountPoint(trashFolder, elsewhere);
            }
        };

        Assert.Throws<IOException>(() => data.DeleteDocument(Record, 1));

        Assert.Empty(Directory.GetFiles(elsewhere));
        Assert.Equal(before, File.ReadAllBytes(data.DocumentPath(Record)));
        Assert.False(File.Exists(data.HistoryPath(Record, 1)), "The delete, refused before the move, left its slot.");
    }

    /// <summary>
    /// Someone holds the document open without delete sharing, so the move waits; during a wait they make the empty trash
    /// folder a mount point and let go of the document. On NTFS the marker of the move keeps the folder from being empty
    /// (<see cref="DeleteOrRestore_WhileItMoves_ItsEmptyDestinationCannotBeMadeALink"/>); where it keeps nothing, the held
    /// folders are checked before each try of the move (security review of M2.5), so the next try is refused and nothing goes
    /// through the link.
    /// </summary>
    [Fact]
    public void DeleteDocument_WhenTheEmptyTrashFolderIsMadeALinkDuringTheWaits_IsRefusedAndMovesNothingThere()
    {
        var files = new TestFileSystem { MarkersKeepNothing = true };
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        var trashFolder = Path.Combine(_data, "trash", "records");
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        TestFolder.SkipWithoutJunctions(trashFolder);
        var holder = new FileStream(data.DocumentPath(Record), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        files.Before = (step, _) =>
        {
            if (step == nameof(IFileSystem.Wait) && files.Waits.Count == 0)
            {
                TestFolder.MakeMountPoint(trashFolder, elsewhere);
                holder.Dispose();
            }
        };

        try
        {
            Assert.Throws<IOException>(() => data.DeleteDocument(Record, 1));
        }
        finally
        {
            holder.Dispose();
        }

        Assert.Empty(Directory.GetFiles(elsewhere));
        Assert.Equal(before, File.ReadAllBytes(data.DocumentPath(Record)));
    }

    /// <summary>
    /// The same during the restore of the only document of a folder: the document folder is empty then, as in a private folder
    /// with one record.
    /// </summary>
    [Fact]
    public void RestoreFromTrash_WhenTheEmptyDocumentFolderIsMadeALinkDuringTheWaits_IsRefusedAndMovesNothingThere()
    {
        var files = new TestFileSystem { MarkersKeepNothing = true };
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        data.DeleteDocument(Record, 1);
        var trash = data.TrashPath(Record);
        var before = File.ReadAllBytes(trash);
        var records = Path.Combine(_data, "records");
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        TestFolder.SkipWithoutJunctions(records);
        var holder = new FileStream(trash, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        files.Before = (step, _) =>
        {
            if (step == nameof(IFileSystem.Wait) && files.Waits.Count == 0)
            {
                TestFolder.MakeMountPoint(records, elsewhere);
                holder.Dispose();
            }
        };

        try
        {
            Assert.Throws<IOException>(() => data.RestoreFromTrash(Record));
        }
        finally
        {
            holder.Dispose();
        }

        Assert.Empty(Directory.GetFiles(elsewhere));
        Assert.Equal(before, File.ReadAllBytes(trash));
    }

    /// <summary>
    /// Someone tries to make the empty destination folder a mount point after the last check before the move. The marker of
    /// the move keeps the folder from being empty, so Windows refuses it, and the move goes where it should (second security
    /// review of M2.5). No marker is left.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeleteOrRestore_WhileItMoves_ItsEmptyDestinationCannotBeMadeALink(bool restore)
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        if (restore)
        {
            data.DeleteDocument(Record, 1);
        }

        var destination = Path.Combine(_data, restore ? "records" : Path.Combine("trash", "records"));
        var moved = restore ? InTrash : Document;
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        TestFolder.SkipWithoutJunctions(elsewhere);
        int? refused = null;
        files.Before = (step, at) =>
        {
            if (step == nameof(IFileSystem.MoveFile) && at.EndsWith(moved, StringComparison.Ordinal) && refused is null)
            {
                refused = TestFolder.TryMakeMountPoint(destination, elsewhere);
            }
        };

        var result = restore ? data.RestoreFromTrash(Record) : data.DeleteDocument(Record, 1);

        Assert.Equal(145, refused);
        Assert.Equal(restore ? WriteOutcome.Written : WriteOutcome.Deleted, result.Outcome);
        Assert.Equal(before, File.ReadAllBytes(restore ? data.DocumentPath(Record) : data.TrashPath(Record)));
        Assert.Empty(Directory.GetFiles(elsewhere));
        Assert.Empty(Directory.GetFiles(destination, "*.tmp"));
    }

    /// <summary>
    /// Where a file server lets a folder that is not empty be made a mount point, the marker keeps nothing, and the move goes
    /// through the link. The check after the move refuses it: the delete or the restore is never given as done. The document
    /// is then where the link pointed, and the slot of the delete stays, which M2.9 restores from.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeleteOrRestore_WhenTheMarkerKeepsNothingAndTheDestinationIsMadeALinkJustBeforeTheMove_IsRefused(bool restore)
    {
        var files = new TestFileSystem { MarkersKeepNothing = true };
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        if (restore)
        {
            data.DeleteDocument(Record, 1);
        }

        var destination = Path.Combine(_data, restore ? "records" : Path.Combine("trash", "records"));
        var moved = restore ? InTrash : Document;
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        TestFolder.SkipWithoutJunctions(elsewhere);
        files.Before = (step, at) =>
        {
            if (step == nameof(IFileSystem.MoveFile) && at.EndsWith(moved, StringComparison.Ordinal) && Directory.GetFiles(elsewhere).Length == 0
                && !File.GetAttributes(destination).HasFlag(FileAttributes.ReparsePoint))
            {
                TestFolder.MakeMountPoint(destination, elsewhere);
            }
        };

        Assert.Throws<IOException>(() => restore ? data.RestoreFromTrash(Record) : data.DeleteDocument(Record, 1));

        Assert.Equal(before, File.ReadAllBytes(Path.Combine(elsewhere, "8f3a1c.json")));
        Assert.Equal(before, File.ReadAllBytes(data.HistoryPath(Record, 1)));
    }

    /// <summary>
    /// During the waits of the move, someone renames the file that is moved away and puts another file, or a junction, at
    /// its name. The move takes that, but its id is not the id of the file that was read, so the delete or the restore is
    /// refused, never given as done (second security review of M2.5). The waits come from a reader of the older copy in the
    /// trash, over which a delete cannot rename, or from a holder of the copy that a restore moves.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DeleteOrRestore_WhenTheFileIsSwappedDuringTheWaits_IsRefused(bool restore, bool junction)
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        var trash = data.TrashPath(Record);
        if (restore)
        {
            data.DeleteDocument(Record, 1);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(trash)!);
            File.WriteAllText(trash, "older");
        }

        var swapped = restore ? trash : data.DocumentPath(Record);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        if (junction)
        {
            TestFolder.SkipWithoutJunctions(elsewhere);
        }

        var holder = restore
            ? new FileStream(trash, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
            : new FileStream(trash, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        files.Before = (step, _) =>
        {
            if (step == nameof(IFileSystem.Wait) && files.Waits.Count == 0)
            {
                holder.Dispose();
                File.Move(swapped, swapped + ".away");
                if (junction)
                {
                    TestFolder.CreateJunction(swapped, elsewhere);
                }
                else
                {
                    File.WriteAllBytes(swapped, DocumentFormat.New(Record, Content("swapped")));
                }
            }
        };

        try
        {
            Assert.Throws<IOException>(() => restore ? data.RestoreFromTrash(Record) : data.DeleteDocument(Record, 1));
        }
        finally
        {
            holder.Dispose();
        }

        Assert.Equal(before, File.ReadAllBytes(swapped + ".away"));
        Assert.Equal(before, File.ReadAllBytes(data.HistoryPath(Record, 1)));
    }

    /// <summary>
    /// The folder that a move takes the document from holds only that document: the document folder of the only record, or
    /// the trash folder of the only deleted one. Right after the last check, someone renames the document out of it and tries
    /// to make the emptied folder a mount point, so the move would take a file of that name from where the link points, maybe
    /// a folder only this user can reach. The marker in the source folder keeps it from being empty, so Windows refuses
    /// (third security review of M2.5); the move finds the document gone, gives <see cref="WriteOutcome.Missing"/>, and leaves
    /// no marker and no slot of its own.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeleteOrRestore_WhileItMoves_ItsEmptiedSourceCannotBeMadeALink(bool restore)
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        if (restore)
        {
            data.DeleteDocument(Record, 1);
        }

        var moved = restore ? data.TrashPath(Record) : data.DocumentPath(Record);
        var source = Path.GetDirectoryName(moved)!;
        var destination = Path.GetDirectoryName(restore ? data.DocumentPath(Record) : data.TrashPath(Record))!;
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        var other = DocumentFormat.New(Record, Content("elsewhere"));
        File.WriteAllBytes(Path.Combine(elsewhere, "8f3a1c.json"), other);
        TestFolder.SkipWithoutJunctions(source);
        int? refused = null;
        files.Before = (step, at) =>
        {
            if (step == nameof(IFileSystem.MoveFile) && at == moved && refused is null)
            {
                File.Move(moved, Path.Combine(_test.Root, "8f3a1c.away"));
                refused = TestFolder.TryMakeMountPoint(source, elsewhere);
            }
        };

        var result = restore ? data.RestoreFromTrash(Record) : data.DeleteDocument(Record, 1);

        Assert.Equal(145, refused);
        Assert.Equal(WriteOutcome.Missing, result.Outcome);
        Assert.Equal(other, File.ReadAllBytes(Path.Combine(elsewhere, "8f3a1c.json")));
        Assert.False(File.Exists(Path.Combine(destination, "8f3a1c.json")), "A file was moved.");
        Assert.Empty(Directory.GetFiles(source, "*.tmp"));
        Assert.Empty(Directory.GetFiles(destination, "*.tmp"));
        Assert.Equal(restore, File.Exists(data.HistoryPath(Record, 1)));
    }

    /// <summary>
    /// The markers are made before the history slot of a delete, so a marker that cannot be made leaves no slot, and the next
    /// delete does not meet one for two minutes (third security review of M2.5); the one made before it goes too.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void DeleteDocument_WhenAMarkerCannotBeMade_LeavesNoSlotNoMarkerAndTheNextDeleteDeletes(int failing)
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var markers = 0;
        files.Before = (step, _) =>
        {
            if (step == nameof(IFileSystem.CreateMarker) && ++markers == failing)
            {
                throw new IOException("The specified network name is no longer available.");
            }
        };

        Assert.Throws<IOException>(() => data.DeleteDocument(Record, 1));

        Assert.False(File.Exists(data.HistoryPath(Record, 1)), "The failed delete left its slot.");
        Assert.Empty(Directory.GetFiles(Path.Combine(_data, "records"), "*.tmp"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_data, "trash", "records"), "*.tmp"));
        files.Before = null;
        Assert.Equal(WriteOutcome.Deleted, data.DeleteDocument(Record, 1).Outcome);
    }

    /// <summary>
    /// A step after the slot and before the move fails: the times of the document cannot be opened, or the check before the
    /// move finds a link. The move was certainly not done, so the delete deletes its own slot, as a busy one does, and the
    /// next delete does not meet it for two minutes (fourth security review of M2.5).
    /// </summary>
    [Fact]
    public void DeleteDocument_WhenAStepBeforeTheMoveFails_LeavesNoSlot()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        files.Before = (step, _) =>
        {
            if (step == nameof(IFileSystem.OpenTimes))
            {
                throw new IOException("The specified network name is no longer available.");
            }
        };

        Assert.Throws<IOException>(() => data.DeleteDocument(Record, 1));

        Assert.False(File.Exists(data.HistoryPath(Record, 1)), "The failed delete left its slot.");
        files.Before = null;
        Assert.Equal(WriteOutcome.Deleted, data.DeleteDocument(Record, 1).Outcome);
    }

    /// <summary>Two machines restore one document at once: the second finds the trash copy gone.</summary>
    [Fact]
    public void RestoreFromTrash_WhenAnotherRestoreIsFirst_GivesMissing()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        data.DeleteDocument(Record, 1);
        WriteResult? first = null;
        files.Before = (step, at) =>
        {
            if (step == nameof(IFileSystem.MoveFile) && at.EndsWith(InTrash, StringComparison.Ordinal) && first is null)
            {
                first = DataFolder.Open(_data, new TestOwners()).RestoreFromTrash(Record);
            }
        };

        Assert.Equal(WriteOutcome.Missing, data.RestoreFromTrash(Record).Outcome);

        Assert.Equal(WriteOutcome.Written, first?.Outcome);
        Assert.Equal(before, File.ReadAllBytes(data.DocumentPath(Record)));
        Assert.False(File.Exists(data.TrashPath(Record)));
    }

    /// <summary>The history folder is empty after a restore took its only slot; a listing through a link is refused.</summary>
    [Fact]
    public void Versions_WhenTheEmptyHistoryFolderIsMadeALinkInPlace_AreRefused()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        data.DeleteDocument(Record, 1);
        data.RestoreFromTrash(Record);
        var slots = SlotFolder(data, Record);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        File.WriteAllBytes(Path.Combine(elsewhere, "v7.json"), DocumentFormat.New(Record, Content("elsewhere")));
        TestFolder.SkipWithoutJunctions(slots);
        files.Before = (step, at) =>
        {
            if (step == nameof(IFileSystem.List) && at == slots)
            {
                TestFolder.MakeMountPoint(slots, elsewhere);
            }
        };

        Assert.Throws<IOException>(() => data.Versions(Record, DateTimeOffset.UtcNow));
    }

    [Theory]
    [MemberData(nameof(Places))]
    public void RestoreFromTrash_PutsTheDocumentBackAtItsVersion_AndItCanBeWrittenAndDeletedAgainAtOnce(bool inPrivate, string? folder, string id)
    {
        var data = Root(inPrivate);
        var key = Key(folder, id);
        data.CreateDocument(key, Content("first"));
        data.ReplaceDocument(key, 1, Content("second"));
        var second = File.ReadAllBytes(data.DocumentPath(key));
        var stamp = StampOf(data.DocumentPath(key));
        data.DeleteDocument(key, 2);

        var result = data.RestoreFromTrash(key);

        Assert.Equal((WriteOutcome.Written, 2), (result.Outcome, result.Version));
        Assert.Equal(second, File.ReadAllBytes(data.DocumentPath(key)));
        Assert.Equal(stamp, result.Stamp);
        Assert.Equal(stamp, StampOf(data.DocumentPath(key)));
        Assert.False(File.Exists(data.TrashPath(key)));

        // The slot of the delete had the version that is back, so it went: the history is as it was before the delete.
        Assert.Equal(["v1.json"], Names(SlotFolder(data, key)));
        Assert.Equal((WriteOutcome.Written, 3), Outcome(data.ReplaceDocument(key, 2, Content("third"))));
        Assert.Equal(second, File.ReadAllBytes(data.HistoryPath(key, 2)));
        Assert.Equal(WriteOutcome.Deleted, data.DeleteDocument(key, 3).Outcome);
        Assert.Equal((WriteOutcome.Written, 3), Outcome(data.RestoreFromTrash(key)));
        Assert.Equal("third", (string?)data.Read(key)?.Document?.Content["name"]);
    }

    /// <summary>
    /// A move into a name that another file left in the last 15 s gets that file's creation time on NTFS (file tunneling,
    /// M2.3). Restored, written and deleted again at once, the new version goes to the name in the trash that the restore has
    /// just left. It keeps its own times, so it does not look edited in place, in the trash or restored from there.
    /// </summary>
    [Fact]
    public void DeleteAndRestore_WithinTheTunnelingOfAnotherFile_KeepTheTimesOfTheDocument()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        data.CreateDocument(Record, Content("first"));
        File.SetCreationTimeUtc(data.DocumentPath(Record), Old);
        File.SetLastWriteTimeUtc(data.DocumentPath(Record), Old);
        data.DeleteDocument(Record, 1);
        data.RestoreFromTrash(Record);
        data.ReplaceDocument(Record, 1, Content("second"));
        var stamp = StampOf(data.DocumentPath(Record));

        Assert.Equal(WriteOutcome.Deleted, data.DeleteDocument(Record, 2).Outcome);
        Assert.Equal(stamp, StampOf(data.TrashPath(Record)));
        Assert.Equal(WriteOutcome.Written, data.RestoreFromTrash(Record).Outcome);
        Assert.Equal(stamp, StampOf(data.DocumentPath(Record)));
        Assert.False(stamp.LooksEditedInPlace);
    }

    [Theory]
    [MemberData(nameof(Places))]
    public void RestoreFromTrash_OverADocumentThatIsThere_GivesExists_AndChangesNothing(bool inPrivate, string? folder, string id)
    {
        var data = Root(inPrivate);
        var key = Key(folder, id);
        data.CreateDocument(key, Content("first"));
        var deleted = File.ReadAllBytes(data.DocumentPath(key));
        data.DeleteDocument(key, 1);
        var live = DocumentFormat.New(key, Content("live"));
        File.WriteAllBytes(data.DocumentPath(key), live);

        Assert.Equal(WriteOutcome.Exists, data.RestoreFromTrash(key).Outcome);

        Assert.Equal(live, File.ReadAllBytes(data.DocumentPath(key)));
        Assert.Equal(deleted, File.ReadAllBytes(data.TrashPath(key)));
        Assert.Equal(deleted, File.ReadAllBytes(data.HistoryPath(key, 1)));
    }

    [Fact]
    public void RestoreFromTrash_WithNothingInTheTrash_GivesMissing()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        Assert.Equal(WriteOutcome.Missing, data.RestoreFromTrash(Record).Outcome);
        Assert.Equal(WriteOutcome.Missing, data.RestoreFromTrash(DocumentKey.AtRoot(DataFolder.Settings)).Outcome);

        data.CreateDocument(Record, Content("first"));
        data.DeleteDocument(Record, 1);
        data.RestoreFromTrash(Record);

        Assert.Equal(WriteOutcome.Missing, data.RestoreFromTrash(DocumentKey.InFolder(DataFolder.Records, "b72e09")).Outcome);
        Assert.Empty(Names(Path.Combine(_data, "trash", "records")));
    }

    [Fact]
    public void RestoreFromTrash_ADamagedCopy_GivesDamaged_AndLeavesItInTheTrash()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        var trash = data.TrashPath(Record);
        Directory.CreateDirectory(Path.GetDirectoryName(trash)!);
        File.WriteAllText(trash, """{"id": "b72e09", "version": 1}""");

        var result = data.RestoreFromTrash(Record);

        Assert.Equal((WriteOutcome.Damaged, DocumentDamage.WrongId), (result.Outcome, result.Damage));
        Assert.True(File.Exists(trash));
        Assert.Null(data.Read(Record));
    }

    /// <summary>A slot with other bytes is not the one the delete made; M2.9 decides how it is cleared (M2.4).</summary>
    [Fact]
    public void RestoreFromTrash_LeavesASlotWithOtherBytes()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        data.CreateDocument(Record, Content("first"));
        data.DeleteDocument(Record, 1);
        var slot = data.HistoryPath(Record, 1);
        var other = DocumentFormat.New(Record, Content("edited in place"));
        File.WriteAllBytes(slot, other);

        Assert.Equal(WriteOutcome.Written, data.RestoreFromTrash(Record).Outcome);

        Assert.Equal(other, File.ReadAllBytes(slot));
        Assert.Equal(WriteOutcome.Conflict, data.ReplaceDocument(Record, 1, Content("second")).Outcome);
    }

    /// <summary>
    /// The delete was long ago, so its slot is old, and another machine saves the restored document before the restore holds
    /// the slot: that write takes the slot over, and it is then the history of its version. The restore sees that the
    /// document has changed and leaves it.
    /// </summary>
    [Fact]
    public void RestoreFromTrash_WhenAWriteTakesOverTheSlotFirst_LeavesTheSlotOfThatWrite()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var first = File.ReadAllBytes(data.DocumentPath(Record));
        data.DeleteDocument(Record, 1);
        var slot = data.HistoryPath(Record, 1);
        File.SetLastWriteTimeUtc(slot, Old);
        WriteResult? theirs = null;
        files.Before = (step, at) =>
        {
            if (step == nameof(IFileSystem.HoldFile) && at.EndsWith(Slot, StringComparison.Ordinal) && theirs is null)
            {
                theirs = DataFolder.Open(_data, new TestOwners()).ReplaceDocument(Record, 1, Content("theirs"));
            }
        };

        Assert.Equal((WriteOutcome.Written, 1), Outcome(data.RestoreFromTrash(Record)));

        Assert.Equal(WriteOutcome.Written, theirs?.Outcome);
        Assert.Equal("theirs", (string?)data.Read(Record)?.Document?.Content["name"]);
        Assert.Equal(first, File.ReadAllBytes(slot));
        Assert.True(File.GetLastWriteTimeUtc(slot) > Old, "The slot is not the one of the other write.");
    }

    [Theory]
    [MemberData(nameof(RestoreSteps))]
    public void RestoreFromTrash_ACrashAtAnyStep_LeavesTheDocumentWholeInTheTrashOrBack(string operation, string path, bool back, bool throws)
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        data.DeleteDocument(Record, 1);
        var calls = 0;
        files.CrashAt = (step, at) => step == operation && at.Contains(path, StringComparison.Ordinal) && ++calls == 1;

        WriteResult? result = null;
        var error = Xunit.Record.Exception(() => result = data.RestoreFromTrash(Record));

        Assert.True(files.Crashed, "The restore did not reach the step.");
        if (throws)
        {
            Assert.IsAssignableFrom<IOException>(error);
        }
        else
        {
            Assert.Null(error);
            Assert.Equal((WriteOutcome.Written, 1), Outcome(result!));
        }

        var next = DataFolder.Open(_data, new TestOwners());
        Assert.Equal(before, File.ReadAllBytes(back ? next.DocumentPath(Record) : next.TrashPath(Record)));
        Assert.False(File.Exists(back ? next.TrashPath(Record) : next.DocumentPath(Record)));
        if (!back)
        {
            Assert.Equal(WriteOutcome.Written, next.RestoreFromTrash(Record).Outcome);
        }

        // The slot of the delete stays when the crash came before it was deleted; a write takes it over once it is old.
        var slot = next.HistoryPath(Record, 1);
        if (File.Exists(slot))
        {
            Assert.Equal(before, File.ReadAllBytes(slot));
            File.SetLastWriteTimeUtc(slot, Old);
        }

        Assert.Equal(WriteOutcome.Written, next.ReplaceDocument(Record, 1, Content("second")).Outcome);
    }

    [Theory]
    [MemberData(nameof(Places))]
    public void Versions_AreTheHistorySlotsAndTheCurrentVersion_InOrder(bool inPrivate, string? folder, string id)
    {
        var data = Root(inPrivate);
        var key = Key(folder, id);
        data.CreateDocument(key, Content("first"));
        data.ReplaceDocument(key, 1, Content("second"));
        data.ReplaceDocument(key, 2, Content("third"));

        var versions = data.Versions(key, DateTimeOffset.UtcNow);

        Assert.Equal(
            [
                new DocumentVersion(1, StampOf(data.HistoryPath(key, 1)), false, TestOwners.Me),
                new DocumentVersion(2, StampOf(data.HistoryPath(key, 2)), false, TestOwners.Me),
                new DocumentVersion(3, StampOf(data.DocumentPath(key)), true, TestOwners.Me),
            ],
            versions);
    }

    /// <summary>Anyone on the share can give a slot a time after the year 9999, which has no stamp to give (M2.7).</summary>
    [Fact]
    public void Versions_ASlotWithATimeAfterTheYear9999_IsSkipped()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        data.CreateDocument(Record, Content("first"));
        data.ReplaceDocument(Record, 1, Content("second"));
        data.ReplaceDocument(Record, 2, Content("third"));
        TestFolder.SetRawTimes(data.HistoryPath(Record, 1), TestFolder.FarFuture, TestFolder.FarFuture);

        Assert.Equal([2, 3], data.Versions(Record, DateTimeOffset.UtcNow).Select(version => version.Version));
    }

    [Fact]
    public void Versions_OfADeletedDocument_AreItsHistorySlots_AndOfANewOneItsCurrentVersion()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        var other = DocumentKey.InFolder(DataFolder.Records, "b72e09");
        data.CreateDocument(Record, Content("first"));
        data.ReplaceDocument(Record, 1, Content("second"));
        data.DeleteDocument(Record, 2);
        data.CreateDocument(other, Content("new"));

        Assert.Equal([1, 2], data.Versions(Record, DateTimeOffset.UtcNow).Select(version => version.Version));
        Assert.DoesNotContain(data.Versions(Record, DateTimeOffset.UtcNow), version => version.IsCurrent);
        Assert.Equal([new DocumentVersion(1, StampOf(data.DocumentPath(other)), true, TestOwners.Me)], data.Versions(other, DateTimeOffset.UtcNow));
        Assert.Empty(data.Versions(DocumentKey.InFolder(DataFolder.Records, "c4d5e6"), DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Another machine saves between the read of the current version and the listing of the slots. The current version is
    /// read first, so its slot is listed too, and no version is missing (code review of M2.5).
    /// </summary>
    [Fact]
    public void Versions_WhenAWriteComesBetweenTheirReads_LeaveNoGap()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        data.ReplaceDocument(Record, 1, Content("second"));
        var saved = false;
        files.Before = (step, at) =>
        {
            if (step == nameof(IFileSystem.OpenRead) && at.EndsWith(Document, StringComparison.Ordinal) && !saved)
            {
                saved = true;
                Assert.Equal(WriteOutcome.Written, DataFolder.Open(_data, new TestOwners()).ReplaceDocument(Record, 2, Content("third")).Outcome);
            }
        };

        var numbers = data.Versions(Record, DateTimeOffset.UtcNow).Select(version => version.Version).Distinct();

        Assert.True(saved, "No write came between the reads.");
        Assert.Equal([1, 2, 3], numbers);
    }

    /// <summary>
    /// Only the names that the layer gives its slots are versions. The temporary files of the slots are skipped, and the old
    /// ones, which a crash leaves, are deleted (M2.3); a link at a slot name is never followed.
    /// </summary>
    [Fact]
    public void Versions_SkipOtherNamesAndLinks_AndDeleteTheOldTemporaryFilesOfTheHistory()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        data.CreateDocument(Record, Content("first"));
        data.ReplaceDocument(Record, 1, Content("second"));
        data.ReplaceDocument(Record, 2, Content("third"));
        var slots = SlotFolder(data, Record);
        foreach (var name in new[] { "v01.json", "v0.json", "V7.json", "v8.json.bak", "x.json", "v99999999999.json", "v-4.json", "v4.tmp" })
        {
            File.WriteAllText(Path.Combine(slots, name), "made up");
        }

        Directory.CreateDirectory(Path.Combine(slots, "v5.json"));
        var now = DateTimeOffset.UtcNow;
        var old = Path.Combine(slots, "v3.1a2b3c4d.tmp");
        var young = Path.Combine(slots, "v3.5e6f7a8b.tmp");
        File.WriteAllText(old, "made up");
        File.WriteAllText(young, "made up");
        File.SetLastWriteTimeUtc(old, (now - DataFolder.TemporaryFileAge - TimeSpan.FromMinutes(1)).UtcDateTime);
        File.SetLastWriteTimeUtc(young, (now - DataFolder.TemporaryFileAge + TimeSpan.FromMinutes(1)).UtcDateTime);
        TestFolder.CreateJunction(Path.Combine(slots, "v6.json"), Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName);

        var versions = data.Versions(Record, now);

        Assert.Equal([(1, false), (2, false), (3, true)], versions.Select(version => (version.Version, version.IsCurrent)));
        Assert.False(File.Exists(old), "The old temporary file is still there.");
        Assert.True(File.Exists(young), "The young temporary file was deleted.");
        // The two slots, the eight other names, the folder, the junction and the young temporary file.
        Assert.Equal(13, Names(slots).Length);
    }

    [Theory]
    [MemberData(nameof(Places))]
    public void ReadVersion_GivesAnOldVersionAsItWasWritten(bool inPrivate, string? folder, string id)
    {
        var data = Root(inPrivate);
        var key = Key(folder, id);
        data.CreateDocument(key, Content("first"));
        data.ReplaceDocument(key, 1, Content("second"));

        var read = data.ReadVersion(key, 1);

        Assert.Equal((1, "first"), (read?.Document?.Version, (string?)read?.Document?.Content["name"]));
        Assert.Null(data.ReadVersion(key, 2));
        Assert.Null(data.ReadVersion(DocumentKey.InFolder(DataFolder.Records, "b72e09"), 1));
        Assert.Null(DataFolder.Open(_data, new TestOwners()).CreatePrivate("other.user").ReadVersion(Record, 1));
    }

    [Fact]
    public void ReadVersion_ASlotWithAnotherVersionOrDamaged_IsDamaged()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        data.CreateDocument(Record, Content("first"));
        data.ReplaceDocument(Record, 1, Content("second"));
        data.ReplaceDocument(Record, 2, Content("third"));
        File.WriteAllBytes(data.HistoryPath(Record, 1), DocumentFormat.Next(Record, 1, Content("first")));
        File.WriteAllText(data.HistoryPath(Record, 2), "{");

        Assert.Equal(DocumentDamage.WrongVersion, data.ReadVersion(Record, 1)?.Damage);
        Assert.Equal(DocumentDamage.NotJson, data.ReadVersion(Record, 2)?.Damage);
    }

    [Fact]
    public void ReadVersion_ASlotThatIsASymbolicLink_IsRefused()
    {
        var data = DataFolder.Create(_data, new TestOwners());
        data.CreateDocument(Record, Content("first"));
        data.ReplaceDocument(Record, 1, Content("second"));
        var target = Path.Combine(_test.Root, "v2.json");
        File.WriteAllBytes(target, DocumentFormat.Next(Record, 1, Content("elsewhere")));
        TestFolder.CreateFileLink(data.HistoryPath(Record, 2), target);

        Assert.Throws<IOException>(() => data.ReadVersion(Record, 2));
    }

    /// <summary>
    /// An old version comes back as a new write of its content, with the check of the version on disk. The upper layer sets
    /// who changed it before the write (M5), so the layer gives the pieces: <see cref="DataFolder.ReadVersion"/> and
    /// <see cref="DataFolder.ReplaceDocument"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void RestoringAnOldVersion_IsANewWriteOfItsContent_WithTheConflictCheck(bool inPrivate, string? folder, string id)
    {
        var data = Root(inPrivate);
        var key = Key(folder, id);
        data.CreateDocument(key, Content("first"));
        data.ReplaceDocument(key, 1, Content("second"));
        data.ReplaceDocument(key, 2, Content("third"));
        var old = data.ReadVersion(key, 1)!.Document!;

        var restored = data.ReplaceDocument(key, 3, old.Content);
        var late = data.ReplaceDocument(key, 3, data.ReadVersion(key, 2)!.Document!.Content);

        Assert.Equal((WriteOutcome.Written, 4), Outcome(restored));
        var read = data.Read(key)?.Document;
        Assert.Equal((4, "first"), (read?.Version, (string?)read?.Content["name"]));
        Assert.Equal((WriteOutcome.Conflict, 4), (late.Outcome, late.OnDisk?.Version));
        Assert.Equal([1, 2, 3, 4], data.Versions(key, DateTimeOffset.UtcNow).Select(version => version.Version));
    }

    /// <summary>
    /// A deleted document keeps its history, so a new document with its id would meet the old slot of version 1 and could
    /// never be written. The id comes back by a restore from the trash.
    /// </summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void CreateDocument_AnIdThatHasHistory_GivesExists_AndWritesNothing(bool inPrivate, string? folder, string id)
    {
        var data = Root(inPrivate);
        var key = Key(folder, id);
        data.CreateDocument(key, Content("first"));
        data.DeleteDocument(key, 1);

        Assert.Equal(WriteOutcome.Exists, data.CreateDocument(key, Content("again")).Outcome);
        File.Delete(data.TrashPath(key));
        Assert.Equal(WriteOutcome.Exists, data.CreateDocument(key, Content("again")).Outcome);

        Assert.False(File.Exists(data.DocumentPath(key)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(data.DocumentPath(key))!, "*.tmp"));
    }

    private static JsonObject Content(string name) => new() { ["template"] = "db", ["name"] = name };

    private static DocumentKey Key(string? folder, string id) => folder is null ? DocumentKey.AtRoot(id) : DocumentKey.InFolder(folder, id);

    private static (WriteOutcome, int) Outcome(WriteResult result) => (result.Outcome, result.Version);

    /// <summary>The folder of the history slots of a document.</summary>
    private static string SlotFolder(DataFolder data, DocumentKey key) => Path.GetDirectoryName(data.HistoryPath(key, 1))!;

    /// <summary>The names in a folder, files and folders, in order.</summary>
    private static string[] Names(string folder) =>
        [.. Directory.GetFileSystemEntries(folder).Select(entry => Path.GetFileName(entry)).Order(StringComparer.Ordinal)];

    private static FileStamp StampOf(string path) =>
        new(new FileInfo(path).Length, File.GetCreationTimeUtc(path), File.GetLastWriteTimeUtc(path));

    /// <summary>A slot as a write or a crash leaves it: the bytes under the name of the slot of version 1, written now or at <paramref name="written"/>.</summary>
    private static string MakeSlot(DataFolder data, DocumentKey key, byte[] bytes, DateTime? written = null)
    {
        var slot = data.HistoryPath(key, 1);
        Directory.CreateDirectory(Path.GetDirectoryName(slot)!);
        File.WriteAllBytes(slot, bytes);
        if (written is { } time)
        {
            File.SetLastWriteTimeUtc(slot, time);
        }

        return slot;
    }

    /// <summary>Runs the writes on threads of their own, released together, as writes from other machines.</summary>
    private static async Task<WriteResult[]> AtOnce(params Func<WriteResult>[] writes)
    {
        using var start = new Barrier(writes.Length);
        return await Task.WhenAll(writes.Select(write => Task.Factory.StartNew(
            () =>
            {
                start.SignalAndWait(Cancel);
                return write();
            },
            Cancel,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)));
    }

    /// <summary>The data folder, or the private folder of a made-up account in it.</summary>
    private DataFolder Root(bool inPrivate)
    {
        var shared = DataFolder.Create(_data, new TestOwners());
        return inPrivate ? shared.CreatePrivate("demo.user") : shared;
    }
}

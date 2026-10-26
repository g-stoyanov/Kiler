using System.Text.Json.Nodes;

namespace Kiler.Storage.Tests;

/// <summary>Creating and replacing documents: the atomic write, the history slot, the file times, the temporary files (M2.3).</summary>
public sealed class DocumentWriteTests : IDisposable
{
    private static readonly DocumentKey Record = DocumentKey.InFolder(DataFolder.Records, "8f3a1c");
    private static readonly DateTime Old = new(2020, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly TestFolder _test = new();
    private readonly string _data;
    private readonly string _records;

    public DocumentWriteTests()
    {
        _data = Path.Combine(_test.Root, "data");
        _records = Path.Combine(_data, "records");
    }

    /// <summary>The temporary file of the document, as the steps get its path.</summary>
    private const string DocumentTemporary = @"\records\8f3a1c.";

    /// <summary>The temporary file of the history slot of version 1.</summary>
    private const string SlotTemporary = @"\8f3a1c\v1.";

    /// <summary>The step, a part of its path, which call of the two, and the version on disk after a crash there (0: none).</summary>
    public static TheoryData<string, string, int, int> CreateSteps => new()
    {
        { nameof(IFileSystem.HoldFolder), @"\records", 1, 0 },
        { nameof(IFileSystem.CreateFile), DocumentTemporary, 1, 0 },
        { nameof(IFileSystem.SetCreationToWriteTime), DocumentTemporary, 1, 0 },
        { nameof(IFileSystem.MoveFile), DocumentTemporary, 1, 0 },
        { nameof(IFileSystem.SetCreationToWriteTime), DocumentTemporary, 2, 1 },
    };

    /// <inheritdoc cref="CreateSteps"/>
    public static TheoryData<string, string, int, int> ReplaceSteps => new()
    {
        { nameof(IFileSystem.HoldFolder), @"\records", 1, 1 },
        { nameof(IFileSystem.OpenRead), @"\records\8f3a1c.json", 1, 1 },
        { nameof(IFileSystem.CreateFile), DocumentTemporary, 1, 1 },
        { nameof(IFileSystem.SetCreationToWriteTime), DocumentTemporary, 1, 1 },
        { nameof(IFileSystem.HoldFolder), @"\history", 1, 1 },
        { nameof(IFileSystem.CreateFolder), @"\history\records", 1, 1 },
        { nameof(IFileSystem.CreateFolder), @"\history\records\8f3a1c", 1, 1 },
        { nameof(IFileSystem.CreateFile), SlotTemporary, 1, 1 },
        { nameof(IFileSystem.MoveFile), SlotTemporary, 1, 1 },
        { nameof(IFileSystem.MoveFile), DocumentTemporary, 1, 1 },
        { nameof(IFileSystem.SetCreationToWriteTime), DocumentTemporary, 2, 2 },
    };

    public void Dispose() => _test.Dispose();

    [Fact]
    public void CreateDocument_ANewId_WritesVersion1()
    {
        var data = DataFolder.Create(_data);
        var content = new JsonObject { ["name"] = "Демо база", ["version"] = 7, ["more"] = new JsonObject { ["port"] = 1521 } };

        var result = data.CreateDocument(Record, content);

        Assert.Equal(WriteOutcome.Written, result.Outcome);
        Assert.Equal(1, result.Version);
        var read = data.Read(Record)?.Document;
        Assert.NotNull(read);
        Assert.Equal(1, read.Version);
        Assert.Equal("Демо база", (string?)read.Content["name"]);
        Assert.Equal(1521, (int?)read.Content["more"]?["port"]);
        Assert.False(read.Content.ContainsKey("version"));
        Assert.Equal(["8f3a1c.json"], Names(_records));
    }

    [Fact]
    public void CreateDocument_AnIdThatExists_GivesExistsAndLeavesTheFile()
    {
        var data = DataFolder.Create(_data);
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));

        var result = data.CreateDocument(Record, Content("second"));

        Assert.Equal(WriteOutcome.Exists, result.Outcome);
        Assert.Equal(0, result.Version);
        Assert.Equal(before, File.ReadAllBytes(data.DocumentPath(Record)));
        Assert.Equal(["8f3a1c.json"], Names(_records));
    }

    [Fact]
    public void CreateDocument_ARootDocumentAndInAPrivateFolder_WritesVersion1()
    {
        var data = DataFolder.Create(_data);
        var settings = DocumentKey.AtRoot(DataFolder.Settings);
        var mine = data.CreatePrivate("demo.user");
        var key = DocumentKey.AtRoot(DataFolder.Key);

        Assert.Equal(WriteOutcome.Written, data.CreateDocument(settings, new JsonObject { ["theme"] = "dark" }).Outcome);
        Assert.Equal(WriteOutcome.Written, mine.CreateDocument(key, new JsonObject { ["wrapped"] = "a2V5" }).Outcome);
        Assert.Equal(WriteOutcome.Written, mine.CreateDocument(Record, Content("лично")).Outcome);

        Assert.Equal(1, data.Read(settings)?.Document?.Version);
        Assert.Equal(1, mine.Read(key)?.Document?.Version);
        Assert.Equal("лично", (string?)mine.Read(Record)?.Document?.Content["name"]);
        Assert.Equal(["8f3a1c.json"], Names(Path.Combine(mine.Root, "records")));
        Assert.Equal(["history", "key.json", "records", "trash"], Names(mine.Root));
    }

    [Fact]
    public void ReplaceDocument_WritesTheNextVersionWhateverTheContentSays()
    {
        var data = DataFolder.Create(_data);
        data.CreateDocument(Record, Content("first"));

        var second = data.ReplaceDocument(Record, 1, new JsonObject { ["id"] = "b72e09", ["version"] = 99, ["name"] = "второ" });
        var third = data.ReplaceDocument(Record, 2, Content("трето"));

        Assert.Equal((WriteOutcome.Written, 2), (second.Outcome, second.Version));
        Assert.Equal((WriteOutcome.Written, 3), (third.Outcome, third.Version));
        var read = data.Read(Record)?.Document;
        Assert.Equal(3, read?.Version);
        Assert.Equal("трето", (string?)read?.Content["name"]);
        Assert.Equal(["8f3a1c.json"], Names(_records));
    }

    [Fact]
    public void ReplaceDocument_PutsEachOldVersionIntoItsHistorySlot()
    {
        var data = DataFolder.Create(_data);
        data.CreateDocument(Record, Content("first"));
        var first = File.ReadAllBytes(data.DocumentPath(Record));
        data.ReplaceDocument(Record, 1, Content("second"));
        var second = File.ReadAllBytes(data.DocumentPath(Record));

        data.ReplaceDocument(Record, 2, Content("third"));

        Assert.Equal(first, File.ReadAllBytes(data.HistoryPath(Record, 1)));
        Assert.Equal(second, File.ReadAllBytes(data.HistoryPath(Record, 2)));
        Assert.Equal(["v1.json", "v2.json"], Names(Path.Combine(_data, "history", "records", "8f3a1c")));
    }

    [Fact]
    public void ReplaceDocument_ARootDocumentAndInAPrivateFolder_KeepTheirHistoryInTheirRoot()
    {
        var data = DataFolder.Create(_data);
        var settings = DocumentKey.AtRoot(DataFolder.Settings);
        var mine = data.CreatePrivate("demo.user");
        data.CreateDocument(settings, new JsonObject { ["theme"] = "dark" });
        mine.CreateDocument(Record, Content("лично"));

        Assert.Equal(2, data.ReplaceDocument(settings, 1, new JsonObject { ["theme"] = "light" }).Version);
        Assert.Equal(2, mine.ReplaceDocument(Record, 1, Content("лично 2")).Version);

        Assert.Equal(["v1.json"], Names(Path.Combine(_data, "history", "settings")));
        Assert.Equal(["v1.json"], Names(Path.Combine(mine.Root, "history", "records", "8f3a1c")));
        Assert.False(Directory.Exists(Path.Combine(_data, "history", "records")));
    }

    [Fact]
    public void ReplaceDocument_AMissingDocument_GivesMissingAndWritesNothing()
    {
        var data = DataFolder.Create(_data);

        Assert.Equal(WriteOutcome.Missing, data.ReplaceDocument(Record, 1, Content("x")).Outcome);

        Assert.Empty(Names(_records));
        Assert.Empty(Names(Path.Combine(_data, "history")));
    }

    [Fact]
    public void ReplaceDocument_ADamagedFile_GivesDamagedAndWritesNothing()
    {
        var data = DataFolder.Create(_data);
        File.WriteAllText(data.DocumentPath(Record), """{"id": "8f3a1c", "version": """);

        var result = data.ReplaceDocument(Record, 1, Content("x"));

        Assert.Equal(WriteOutcome.Damaged, result.Outcome);
        Assert.Equal(DocumentDamage.NotJson, result.Damage);
        Assert.Equal("""{"id": "8f3a1c", "version": """, File.ReadAllText(data.DocumentPath(Record)));
        Assert.Equal(["8f3a1c.json"], Names(_records));
        Assert.Empty(Names(Path.Combine(_data, "history")));
    }

    [Fact]
    public void ReplaceDocument_AnotherVersionOnDisk_GivesConflictWithTheDocumentOnDisk()
    {
        var data = DataFolder.Create(_data);
        data.CreateDocument(Record, Content("first"));
        data.ReplaceDocument(Record, 1, Content("second"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));

        var result = data.ReplaceDocument(Record, 1, Content("late"));

        Assert.Equal(WriteOutcome.Conflict, result.Outcome);
        Assert.Equal(2, result.OnDisk?.Version);
        Assert.Equal("second", (string?)result.OnDisk?.Content["name"]);
        Assert.Equal(before, File.ReadAllBytes(data.DocumentPath(Record)));
        Assert.Equal(["v1.json"], Names(Path.Combine(_data, "history", "records", "8f3a1c")));
        Assert.Equal(["8f3a1c.json"], Names(_records));
    }

    /// <summary>Another write of the same version is under way, or one stopped there; M2.4 takes over an old leftover.</summary>
    [Fact]
    public void ReplaceDocument_WhenItsHistorySlotExists_GivesConflictAndWritesNothing()
    {
        var data = DataFolder.Create(_data);
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        var slot = data.HistoryPath(Record, 1);
        Directory.CreateDirectory(Path.GetDirectoryName(slot)!);
        File.WriteAllText(slot, "made up");

        var result = data.ReplaceDocument(Record, 1, Content("second"));

        Assert.Equal(WriteOutcome.Conflict, result.Outcome);
        Assert.Equal(1, result.OnDisk?.Version);
        Assert.Equal(before, File.ReadAllBytes(data.DocumentPath(Record)));
        Assert.Equal("made up", File.ReadAllText(slot));
        Assert.Equal(["8f3a1c.json"], Names(_records));
    }

    [Fact]
    public void ReplaceDocument_AtTheLastVersion_GivesNoNextVersionAndLeavesNoSlot()
    {
        var data = DataFolder.Create(_data);
        File.WriteAllBytes(data.DocumentPath(Record), DocumentFormat.Next(Record, int.MaxValue - 1, Content("last")));

        var result = data.ReplaceDocument(Record, int.MaxValue, Content("after"));

        Assert.Equal(WriteOutcome.NoNextVersion, result.Outcome);
        Assert.Equal(int.MaxValue, data.Read(Record)?.Document?.Version);
        Assert.Equal(["8f3a1c.json"], Names(_records));
        Assert.Empty(Names(Path.Combine(_data, "history")));
    }

    [Fact]
    public void Write_ContentOverTheLimit_IsRefusedBeforeAnythingIsWritten()
    {
        var data = DataFolder.Create(_data);
        data.CreateDocument(Record, Content("first"));
        var large = new JsonObject { ["text"] = new string('a', DocumentFormat.MaxSize) };

        Assert.Throws<ArgumentException>(() => data.CreateDocument(DocumentKey.InFolder(DataFolder.Records, "b72e09"), large));
        Assert.Throws<ArgumentException>(() => data.ReplaceDocument(Record, 1, large));

        Assert.Equal(1, data.Read(Record)?.Document?.Version);
        Assert.Equal(["8f3a1c.json"], Names(_records));
        Assert.Empty(Names(Path.Combine(_data, "history")));
    }

    [Theory]
    [MemberData(nameof(CreateSteps))]
    public void CreateDocument_ACrashAtAnyStep_LeavesNoHalfWrittenDocument(string operation, string path, int occurrence, int version)
    {
        var files = new TestFileSystem { WriteTakes = TimeSpan.FromSeconds(1) };
        var data = DataFolder.Create(_data, files);
        var calls = 0;
        files.CrashAt = (step, at) => step == operation && at.Contains(path, StringComparison.Ordinal) && ++calls == occurrence;

        Assert.ThrowsAny<IOException>(() => data.CreateDocument(Record, Content("first")));

        Assert.True(files.Crashed, "The write did not reach the step.");
        AssertWholeDocuments(version == 0 ? null : DocumentFormat.New(Record, Content("first")), version);
    }

    [Theory]
    [MemberData(nameof(ReplaceSteps))]
    public void ReplaceDocument_ACrashAtAnyStep_LeavesTheOldOrTheNewDocumentWhole(string operation, string path, int occurrence, int version)
    {
        var files = new TestFileSystem { WriteTakes = TimeSpan.FromSeconds(1) };
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var old = File.ReadAllBytes(data.DocumentPath(Record));
        var calls = 0;
        files.CrashAt = (step, at) => step == operation && at.Contains(path, StringComparison.Ordinal) && ++calls == occurrence;

        Assert.ThrowsAny<IOException>(() => data.ReplaceDocument(Record, 1, Content("second")));

        Assert.True(files.Crashed, "The write did not reach the step.");
        AssertWholeDocuments(version == 1 ? old : DocumentFormat.Next(Record, 1, Content("second")), version);

        // A slot is never left half written under its name: whole, or not there.
        var slot = Path.Combine(_data, "history", "records", "8f3a1c", "v1.json");
        Assert.True(!File.Exists(slot) || File.ReadAllBytes(slot).SequenceEqual(old), "The slot is not the old version.");
    }

    /// <summary>Someone holds the document open: access denied, as measured on NTFS, or a sharing violation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplaceDocument_WhenTheRenameIsRefusedForAWhile_TriesAgainAndWrites(bool sharingViolation)
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var refusals = 0;
        files.Before = (step, at) =>
        {
            if (step == nameof(IFileSystem.MoveFile) && at.Contains(DocumentTemporary, StringComparison.Ordinal) && refusals++ < 3)
            {
                throw sharingViolation
                    ? new IOException("The process cannot access the file because it is being used by another process.", unchecked((int)0x80070020))
                    : new UnauthorizedAccessException("Access to the path is denied.");
            }
        };

        var result = data.ReplaceDocument(Record, 1, Content("second"));

        Assert.Equal(WriteOutcome.Written, result.Outcome);
        Assert.Equal(DataFolder.RenameWaits.Take(3), files.Waits);
        Assert.Equal(2, data.Read(Record)?.Document?.Version);
    }

    [Fact]
    public void ReplaceDocument_WhileAReaderHoldsTheFileOpen_GivesBusyAndChangesNothing()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var path = data.DocumentPath(Record);
        var before = File.ReadAllBytes(path);

        WriteResult result;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            result = data.ReplaceDocument(Record, 1, Content("second"));
        }

        Assert.Equal(WriteOutcome.Busy, result.Outcome);
        Assert.Equal(DataFolder.RenameWaits, files.Waits);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(["8f3a1c.json"], Names(_records));
        // The slot went with it, so the write can be tried again at once.
        Assert.Empty(Names(Path.Combine(_data, "history", "records", "8f3a1c")));
        Assert.Equal(WriteOutcome.Written, data.ReplaceDocument(Record, 1, Content("again")).Outcome);
    }

    /// <summary>The rename may have been done when its answer was lost; then the slot is the right history (M2.4).</summary>
    [Fact]
    public void ReplaceDocument_WhenTheRenameFailsOtherwise_KeepsTheSlotAndDeletesTheTemporaryFile()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        files.Before = (step, at) =>
        {
            if (step == nameof(IFileSystem.MoveFile) && at.Contains(DocumentTemporary, StringComparison.Ordinal))
            {
                throw new IOException("The specified network name is no longer available.");
            }
        };

        Assert.Throws<IOException>(() => data.ReplaceDocument(Record, 1, Content("second")));

        Assert.Empty(files.Waits);
        Assert.Equal(before, File.ReadAllBytes(data.DocumentPath(Record)));
        Assert.Equal(before, File.ReadAllBytes(data.HistoryPath(Record, 1)));
        Assert.Equal(["8f3a1c.json"], Names(_records));
        Assert.Equal(["v1.json"], Names(Path.Combine(_data, "history", "records", "8f3a1c")));
    }

    [Fact]
    public void ReplaceDocument_TheNewDocumentCanBeReadWhileItsTimesAreSet()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        DocumentRead? read = null;
        var calls = 0;
        files.Before = (step, _) =>
        {
            // Another Kiler reads the document right after the rename, while the write still holds the file.
            if (step == nameof(IFileSystem.SetCreationToWriteTime) && ++calls == 2)
            {
                read = DataFolder.Open(_data).Read(Record);
            }
        };

        Assert.Equal(WriteOutcome.Written, data.ReplaceDocument(Record, 1, Content("second")).Outcome);

        Assert.Equal(2, read?.Document?.Version);
    }

    [Fact]
    public void ReplaceDocument_WhenASymbolicLinkWithoutTargetIsAtTheSlotName_GivesConflictAndDoesNotFollowIt()
    {
        var data = DataFolder.Create(_data);
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        var target = Path.Combine(Directory.CreateDirectory(Path.Combine(_test.Root, "outside")).FullName, "v1.json");
        var slot = data.HistoryPath(Record, 1);
        Directory.CreateDirectory(Path.GetDirectoryName(slot)!);
        TestFolder.CreateFileLink(slot, target);

        Assert.Equal(WriteOutcome.Conflict, data.ReplaceDocument(Record, 1, Content("second")).Outcome);

        Assert.False(File.Exists(target), "The write followed the link.");
        Assert.Equal(before, File.ReadAllBytes(data.DocumentPath(Record)));
        Assert.Equal(["v1.json"], Names(Path.GetDirectoryName(slot)!));
    }

    [Fact]
    public void ReplaceDocument_WhenAJunctionWithoutTargetIsAtTheSlotName_GivesConflictAndDoesNotFollowIt()
    {
        var data = DataFolder.Create(_data);
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        var target = Directory.CreateDirectory(Path.Combine(_test.Root, "outside", "gone")).FullName;
        var slot = data.HistoryPath(Record, 1);
        Directory.CreateDirectory(Path.GetDirectoryName(slot)!);
        TestFolder.CreateJunction(slot, target);
        Directory.Delete(target);

        Assert.Equal(WriteOutcome.Conflict, data.ReplaceDocument(Record, 1, Content("second")).Outcome);

        Assert.False(Path.Exists(target), "The write followed the junction.");
        Assert.Equal(before, File.ReadAllBytes(data.DocumentPath(Record)));
    }

    /// <summary>
    /// The hold does not stop it (<c>PinnedFolderTests</c>). NTFS refuses the create-new of the temporary file through a
    /// mount point made in place (measured in M2.3); on a file server that lets it through, the check of the held folders
    /// refuses the write before its renames.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplaceDocument_WhenAHeldEmptyFolderIsMadeALinkInPlace_IsRefusedAndChangesNothing(bool createThroughLinks)
    {
        var files = new TestFileSystem { CreateThroughLinks = createThroughLinks };
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        var slotFolder = Path.Combine(_data, "history", "records", "8f3a1c");
        TestFolder.SkipWithoutJunctions(slotFolder);
        files.Before = (step, at) =>
        {
            // The slot folder is new, empty and held when the old version is written into it.
            if (step == nameof(IFileSystem.CreateFile) && at.Contains(SlotTemporary, StringComparison.Ordinal))
            {
                TestFolder.MakeMountPoint(slotFolder, elsewhere);
            }
        };

        Assert.Throws<IOException>(() => data.ReplaceDocument(Record, 1, Content("second")));

        Assert.Equal(before, File.ReadAllBytes(data.DocumentPath(Record)));
        Assert.Empty(Directory.GetFiles(elsewhere));
        Assert.Equal(["8f3a1c.json"], Names(_records));
    }

    /// <inheritdoc cref="ReplaceDocument_WhenAHeldEmptyFolderIsMadeALinkInPlace_IsRefusedAndChangesNothing"/>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateDocument_WhenTheEmptyDocumentFolderIsMadeALinkInPlace_IsRefused(bool createThroughLinks)
    {
        var files = new TestFileSystem { CreateThroughLinks = createThroughLinks };
        var data = DataFolder.Create(_data, files);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        TestFolder.SkipWithoutJunctions(_records);
        files.Before = (step, at) =>
        {
            if (step == nameof(IFileSystem.CreateFile) && at.Contains(DocumentTemporary, StringComparison.Ordinal))
            {
                TestFolder.MakeMountPoint(_records, elsewhere);
            }
        };

        Assert.Throws<IOException>(() => data.CreateDocument(Record, Content("first")));

        Assert.Empty(Directory.GetFiles(elsewhere));
    }

    /// <summary>A read goes through a mount point; the check of the held folders after it refuses what it read.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadOrReplace_WhenTheEmptyDocumentFolderIsMadeALinkInPlace_IsRefusedAndWritesNothing(bool replace)
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        File.WriteAllBytes(Path.Combine(elsewhere, "8f3a1c.json"), DocumentFormat.New(Record, Content("elsewhere")));
        TestFolder.SkipWithoutJunctions(_records);
        var created = false;
        files.Before = (step, _) =>
        {
            if (step == nameof(IFileSystem.OpenRead))
            {
                TestFolder.MakeMountPoint(_records, elsewhere);
            }

            created |= step == nameof(IFileSystem.CreateFile);
        };

        Assert.Throws<IOException>(() => replace ? data.ReplaceDocument(Record, 1, Content("second")) : data.Read(Record));

        Assert.False(created, "The write made a file with what it read through the link.");
    }

    /// <summary>Nothing holds the slot during the waits of a rename, so someone can delete it and make its folder a link.</summary>
    [Fact]
    public void ReplaceDocument_WhenTheSlotFolderIsMadeALinkDuringTheWaits_DeletesNothingThroughIt()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        var slot = data.HistoryPath(Record, 1);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        var other = Path.Combine(elsewhere, "v1.json");
        File.WriteAllText(other, "made up");
        TestFolder.SkipWithoutJunctions(slot);
        var waits = 0;
        files.Before = (step, _) =>
        {
            if (step == nameof(IFileSystem.Wait) && waits++ == 0)
            {
                File.Delete(slot);
                TestFolder.MakeMountPoint(Path.GetDirectoryName(slot)!, elsewhere);
            }
        };

        using (new FileStream(data.DocumentPath(Record), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.Throws<IOException>(() => data.ReplaceDocument(Record, 1, Content("second")));
        }

        Assert.Equal("made up", File.ReadAllText(other));
    }

    /// <summary>A path over 260 characters, which Windows takes only with the <c>\\?\</c> prefix.</summary>
    [Fact]
    public void Write_InADataFolderWithALongPath_Works()
    {
        var above = _test.Root;
        while (above.Length < 240)
        {
            above = Path.Combine(above, new string('d', 40));
        }

        Directory.CreateDirectory(above);
        var data = DataFolder.Create(Path.Combine(above, "data"));

        Assert.Equal(WriteOutcome.Written, data.CreateDocument(Record, Content("first")).Outcome);
        Assert.Equal(WriteOutcome.Written, data.ReplaceDocument(Record, 1, Content("second")).Outcome);

        Assert.True(data.HistoryPath(Record, 1).Length > 260, "The path is not long.");
        Assert.Equal(2, data.Read(Record)?.Document?.Version);
        Assert.True(File.Exists(data.HistoryPath(Record, 1)));
    }

    [Fact]
    public void Write_ANewFile_HasTheCreationTimeOfItsWriteTime()
    {
        // Without the times set, a file that takes a while to write has a creation time earlier than its write time.
        var files = new TestFileSystem { WriteTakes = TimeSpan.FromSeconds(1) };
        var data = DataFolder.Create(_data, files);
        var path = data.DocumentPath(Record);

        var created = data.CreateDocument(Record, Content("first"));
        var createdStamp = StampOf(path);
        var replaced = data.ReplaceDocument(Record, 1, Content("second"));

        Assert.Equal(createdStamp, created.Stamp);
        Assert.Equal(createdStamp.LastWriteTime, createdStamp.CreationTime);
        Assert.Equal(StampOf(path), replaced.Stamp);
        Assert.Equal(StampOf(path).LastWriteTime, StampOf(path).CreationTime);
        Assert.False(StampOf(path).LooksEditedInPlace);
    }

    /// <summary>
    /// NTFS gives a file the creation time of a file that had its name in the last 15 s (file tunneling). Moving the old
    /// file away and back leaves its creation time in that cache.
    /// </summary>
    [Fact]
    public void ReplaceDocument_WithinTheTunnelingOfAnOldFile_HasTheCreationTimeOfItsWriteTime()
    {
        var data = DataFolder.Create(_data);
        var path = data.DocumentPath(Record);
        data.CreateDocument(Record, Content("first"));
        File.SetCreationTimeUtc(path, Old);
        File.SetLastWriteTimeUtc(path, Old);
        File.Move(path, path + ".away");
        File.Move(path + ".away", path);

        var result = data.ReplaceDocument(Record, 1, Content("second"));

        var stamp = StampOf(path);
        Assert.Equal(stamp, result.Stamp);
        Assert.Equal(stamp.LastWriteTime, stamp.CreationTime);
        Assert.True(stamp.CreationTime > Old, "The new file has the creation time of the old one.");
    }

    [Fact]
    public void CreateDocument_WithinTheTunnelingOfADeletedFile_HasTheCreationTimeOfItsWriteTime()
    {
        var data = DataFolder.Create(_data);
        var path = data.DocumentPath(Record);
        File.WriteAllText(path, "made up");
        File.SetCreationTimeUtc(path, Old);
        File.Delete(path);

        var result = data.CreateDocument(Record, Content("first"));

        var stamp = StampOf(path);
        Assert.Equal(stamp, result.Stamp);
        Assert.Equal(stamp.LastWriteTime, stamp.CreationTime);
    }

    [Fact]
    public void List_AFileEditedInPlace_LooksEditedInPlace_AndAFileOfTheLayerDoesNot()
    {
        var data = DataFolder.Create(_data);
        var other = DocumentKey.InFolder(DataFolder.Records, "b72e09");
        data.CreateDocument(Record, Content("first"));
        data.CreateDocument(other, Content("other"));
        data.ReplaceDocument(other, 1, Content("other, second"));

        // As Notepad saves: into the file it opened, a moment later.
        Thread.Sleep(50);
        using (var file = new FileStream(data.DocumentPath(Record), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            file.Seek(0, SeekOrigin.End);
            file.Write(" "u8);
        }

        var listed = data.List(DataFolder.Records).Documents.ToDictionary(document => document.Key, document => document.File.Stamp);
        Assert.True(listed[Record].LooksEditedInPlace);
        Assert.False(listed[other].LooksEditedInPlace);
    }

    [Fact]
    public void List_GivesTheTemporaryFilesApart_AndNeverAsDocuments()
    {
        var data = DataFolder.Create(_data);
        data.CreateDocument(Record, Content("first"));
        // A whole document under a temporary name, as a crash before the rename leaves it.
        File.Copy(data.DocumentPath(Record), Path.Combine(_records, "8f3a1c.1a2b3c4d.tmp"));
        File.WriteAllText(Path.Combine(_records, "notes.tmp"), "made up");
        File.WriteAllText(Path.Combine(_records, "8f3a1c.zz.tmp"), "made up");
        File.WriteAllText(Path.Combine(_data, "settings.0f1e2d3c.tmp"), "made up");

        var records = data.List(DataFolder.Records);
        var root = data.List(null);

        Assert.Equal([Record], records.Documents.Select(document => document.Key));
        Assert.Equal(["8f3a1c.1a2b3c4d.tmp"], records.Temporary.Select(entry => entry.Name));
        Assert.Empty(records.BadNames);
        Assert.Empty(root.Documents);
        Assert.Equal(["settings.0f1e2d3c.tmp"], root.Temporary.Select(entry => entry.Name));
    }

    [Fact]
    public void DeleteOldTemporaryFiles_DeletesOnlyTheTemporaryFilesOverTheAge()
    {
        var data = DataFolder.Create(_data);
        data.CreateDocument(Record, Content("first"));
        var now = DateTimeOffset.UtcNow;
        var old = Path.Combine(_records, "8f3a1c.1a2b3c4d.tmp");
        var young = Path.Combine(_records, "8f3a1c.5e6f7a8b.tmp");
        File.WriteAllText(old, "made up");
        File.WriteAllText(young, "made up");
        File.WriteAllText(Path.Combine(_records, "notes.tmp"), "made up");
        File.SetLastWriteTimeUtc(old, (now - DataFolder.TemporaryFileAge - TimeSpan.FromMinutes(1)).UtcDateTime);
        File.SetLastWriteTimeUtc(young, (now - DataFolder.TemporaryFileAge + TimeSpan.FromMinutes(1)).UtcDateTime);
        File.SetLastWriteTimeUtc(data.DocumentPath(Record), Old);

        var deleted = data.DeleteOldTemporaryFiles(DataFolder.Records, data.List(DataFolder.Records), now);

        Assert.Equal(1, deleted);
        Assert.Equal(["8f3a1c.5e6f7a8b.tmp", "8f3a1c.json", "notes.tmp"], Names(_records));
    }

    [Fact]
    public void Write_WhenTheDocumentFolderIsAJunction_IsRefusedAndWritesNothingThere()
    {
        var data = DataFolder.Create(_data);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        File.WriteAllBytes(Path.Combine(elsewhere, "8f3a1c.json"), DocumentFormat.New(Record, Content("elsewhere")));
        var before = File.ReadAllBytes(Path.Combine(elsewhere, "8f3a1c.json"));
        Directory.Delete(_records);
        TestFolder.CreateJunction(_records, elsewhere);

        Assert.Throws<IOException>(() => data.CreateDocument(DocumentKey.InFolder(DataFolder.Records, "b72e09"), Content("x")));
        Assert.Throws<IOException>(() => data.ReplaceDocument(Record, 1, Content("x")));
        Assert.Throws<IOException>(() => data.Read(Record));

        Assert.Equal(["8f3a1c.json"], Names(elsewhere));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(elsewhere, "8f3a1c.json")));
        Assert.Empty(Names(Path.Combine(_data, "history")));
    }

    [Theory]
    [InlineData("history")]
    [InlineData(@"history\records")]
    [InlineData(@"history\records\8f3a1c")]
    public void ReplaceDocument_WhenAHistoryFolderIsAJunction_IsRefusedAndChangesNothing(string folder)
    {
        var data = DataFolder.Create(_data);
        data.CreateDocument(Record, Content("first"));
        var before = File.ReadAllBytes(data.DocumentPath(Record));
        var link = Path.Combine(_data, folder);

        // The junction points to a folder that has the rest of the history path, so a write that follows it finds its way.
        var elsewhere = Path.Combine(_test.Root, "elsewhere");
        Directory.CreateDirectory(Path.Combine(elsewhere, Path.GetRelativePath(link, Path.Combine(_data, "history", "records", "8f3a1c"))));
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (Directory.Exists(link))
        {
            Directory.Delete(link);
        }

        TestFolder.CreateJunction(link, elsewhere);

        Assert.Throws<IOException>(() => data.ReplaceDocument(Record, 1, Content("second")));

        Assert.Equal(before, File.ReadAllBytes(data.DocumentPath(Record)));
        Assert.Empty(Directory.GetFiles(elsewhere, "*", SearchOption.AllDirectories));
        Assert.Equal(["8f3a1c.json"], Names(_records));
    }

    [Fact]
    public void ReplaceDocument_HoldsItsFolders_SoNoneCanBeTurnedIntoALinkWhileItWrites()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        string[] held = [_data, _records, Path.Combine(_data, "history"), Path.Combine(_data, "history", "records"), Path.Combine(_data, "history", "records", "8f3a1c")];
        var renamed = new List<string>();
        files.Before = (step, _) =>
        {
            if (step != nameof(IFileSystem.MoveFile))
            {
                return;
            }

            foreach (var folder in held)
            {
                if (Xunit.Record.Exception(() => Directory.Move(folder, folder + "-moved")) is null)
                {
                    renamed.Add(folder);
                    Directory.Move(folder + "-moved", folder);
                }
            }
        };

        Assert.Equal(WriteOutcome.Written, data.ReplaceDocument(Record, 1, Content("second")).Outcome);

        Assert.Empty(renamed);
    }

    [Fact]
    public void Read_ADocumentThatIsASymbolicLink_IsRefused()
    {
        var data = DataFolder.Create(_data);
        var target = Path.Combine(_test.Root, "8f3a1c.json");
        File.WriteAllBytes(target, DocumentFormat.New(Record, Content("elsewhere")));
        TestFolder.CreateFileLink(data.DocumentPath(Record), target);

        Assert.Throws<IOException>(() => data.Read(Record));
        Assert.Throws<IOException>(() => data.ReplaceDocument(Record, 1, Content("x")));
        Assert.Equal(WriteOutcome.Exists, data.CreateDocument(Record, Content("x")).Outcome);
    }

    private static JsonObject Content(string name) => new() { ["template"] = "db", ["name"] = name };

    /// <summary>The names in a folder, files and folders, in order.</summary>
    private static string[] Names(string folder) =>
        [.. Directory.GetFileSystemEntries(folder).Select(entry => Path.GetFileName(entry)).Order(StringComparer.Ordinal)];

    private static FileStamp StampOf(string path) =>
        new(new FileInfo(path).Length, File.GetCreationTimeUtc(path), File.GetLastWriteTimeUtc(path));

    /// <summary>
    /// After a crash, read with the real file operations as the next start reads: the document is whole, with the expected
    /// bytes or missing, and every <c>.json</c> name in its folder is a whole document. A file of the layer never looks edited
    /// in place, also when the crash came before its times were set after the rename. The next write of the document gives a
    /// defined outcome: a conflict only on a whole slot that the crash left, which M2.4 takes over.
    /// </summary>
    private void AssertWholeDocuments(byte[]? expected, int version)
    {
        AssertWholeDocuments(expected);
        var data = DataFolder.Open(_data);
        var slotLeft = version > 0 && File.Exists(data.HistoryPath(Record, version));
        var next = version == 0 ? data.CreateDocument(Record, Content("again")) : data.ReplaceDocument(Record, version, Content("again"));
        Assert.Equal(slotLeft ? WriteOutcome.Conflict : WriteOutcome.Written, next.Outcome);
    }

    private void AssertWholeDocuments(byte[]? expected)
    {
        var data = DataFolder.Open(_data);
        var path = data.DocumentPath(Record);
        if (expected is null)
        {
            Assert.Null(data.Read(Record));
        }
        else
        {
            Assert.Equal(expected, File.ReadAllBytes(path));
            Assert.False(StampOf(path).LooksEditedInPlace);
        }

        var listing = data.List(DataFolder.Records);
        Assert.Equal(expected is null ? [] : [Record], listing.Documents.Select(document => document.Key));
        Assert.All(listing.Documents, document => Assert.False(data.Read(document.Key)?.IsDamaged));
    }
}

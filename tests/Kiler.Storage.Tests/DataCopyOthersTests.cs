using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Storage.Tests;

/// <summary>
/// The private folders of other users in the local copy of an admin (M2.7c): those of the accounts that the copy is given are
/// copied for a restore (M2.9), with only the head of each document read, in a budget of their own with a cap for each, and
/// are never given to the upper layers; one that the copy is no longer given stays in it as it is.
/// </summary>
public sealed class DataCopyOthersTests : IDisposable
{
    private const string Me = "demo.user";
    private const string Other = "demo.other";
    private const string Third = "demo.third";

    private static readonly DocumentKey Record = DocumentKey.InFolder(DataFolder.Records, "8f3a1c");
    private static readonly DocumentKey OtherRecord = DocumentKey.InFolder(DataFolder.Records, "b72e09");
    private static readonly DocumentKey Key = DocumentKey.AtRoot(DataFolder.Key);
    private static readonly DocumentKey TheirRecord = Record.InPrivate(Other);

    private readonly TestFolder _test = new();
    private readonly TestFileSystem _files = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly DataFolder _share;
    private readonly string _copy;

    public DataCopyOthersTests()
    {
        _share = DataFolder.Create(Path.Combine(_test.Root, "data"), _files);
        _copy = Directory.CreateDirectory(Path.Combine(_test.Root, "copy")).FullName;
    }

    public void Dispose() => _test.Dispose();

    [Fact]
    public void Load_ThePrivateFoldersOfTheAccountsTheCopyIsGiven_AreCopied_AndNotGiven()
    {
        WriteShared(Record, "shared");
        Create(_share.CreatePrivate(Me), Record, "mine");
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "other");
        Create(other, Key, "key");
        Create(_share.CreatePrivate(Third), Record, "third");
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);

        var load = copy.Load();

        Assert.Equal(Keys([Record, Record.InPrivate(Me)]), Keys(load.Changed));
        Assert.Empty(load.Gone);
        Assert.Empty(load.Outside);
        Assert.Empty(load.Skipped);
        Assert.Equal(File.ReadAllBytes(other.DocumentPath(Record)), File.ReadAllBytes(PrivateCopyPath(Other, Record)));
        Assert.Equal(File.ReadAllBytes(other.DocumentPath(Key)), File.ReadAllBytes(PrivateCopyPath(Other, Key)));
        Assert.False(Directory.Exists(PrivateCopyRoot(Third)), "A private folder that the copy was not given is in it.");
        Assert.Equal(Keys([Record, Record.InPrivate(Me)]), Keys(copy.Documents()));
        Assert.Equal(Keys([Record, Record.InPrivate(Me)]), Keys(Open(Me).Documents()));

        // The next load that takes them compares them by their listing, as those of the user.
        _files.Reads = 0;
        var listed = ListedUnder(other.Root);
        Assert.Empty(LoadAMinuteLater(copy).Changed);
        Assert.NotEmpty(listed);
        Assert.Equal(0, _files.Reads);
    }

    [Fact]
    public void Load_BeforeTheCopyIsGivenTheOtherAccounts_LoadsNoOtherPrivateFolder()
    {
        Create(_share.CreatePrivate(Other), Record, "other");
        var copy = Open(Me);
        _files.Reads = 0;

        var load = copy.Load();

        Assert.Equal(0, _files.Reads);
        Assert.Empty(load.Skipped);
        Assert.False(Directory.Exists(PrivateCopyRoot(Other)), "The private folder of another user is in the copy.");
    }

    [Fact]
    public void ReadFolder_OfTheHeadsOnly_GivesTheVersionAndTheOwner_AndNoDocument()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "other");
        using var listed = other.ListFolder(DataFolder.Records, inTrash: false, maxEntries: 100, maxReported: 100);

        var file = Assert.Single(other.ReadFolder(listed, listed.Listing.Documents, DateTimeOffset.UtcNow, headsOnly: true));

        Assert.Null(file.Read);
        Assert.Equal(new DocumentHead(1, null, TestOwners.Me), file.Head);
        Assert.Equal(File.ReadAllBytes(other.DocumentPath(Record)), file.Bytes);
    }

    [Fact]
    public void Load_ADocumentOfAnotherPrivateFolderDeletedOutsideKiler_IsReported_WithItsLastGoodBytes_AndIsNotGiven()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "other");
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);
        copy.Load();
        var good = File.ReadAllBytes(other.DocumentPath(Record));
        File.Delete(other.DocumentPath(Record));

        var load = LoadAMinuteLater(copy);

        Assert.Empty(load.Gone);
        Assert.Equal(good, File.ReadAllBytes(PrivateKeptPath(Other, Record)));
        Assert.False(File.Exists(PrivateCopyPath(Other, Record)), "The gone document is still in data.");
        _clock.Advance(InPlaceEdits.SettleTime);
        Assert.Equal(new OutsideChange(TheirRecord, InTrash: false, OutsideReason.Gone, LastGood: 1), Assert.Single(copy.Load().Outside));
    }

    [Fact]
    public void Load_ADocumentOfAnotherPrivateFolderDamagedInsideItsContent_IsDamaged_AsForItsOwner()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "other");
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);
        copy.Load();
        var good = File.ReadAllBytes(other.DocumentPath(Record));

        // A name twice deep in the content, which only a check of the whole file finds.
        TestFolder.WriteAsKiler(other.DocumentPath(Record), """{"id": "8f3a1c", "version": 2, "private": {"a": 1, "a": 2}}"""u8.ToArray());
        var load = LoadAMinuteLater(copy);

        Assert.Equal(new OutsideChange(TheirRecord, InTrash: false, OutsideReason.Damaged, LastGood: 1), Assert.Single(load.Outside));
        Assert.Equal(good, File.ReadAllBytes(PrivateKeptPath(Other, Record)));
        Assert.Empty(load.Changed);
    }

    [Fact]
    public void Load_FollowsTheWritesOfKilerInAnotherPrivateFolder_AndListsNoHistoryOfIt()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "first");
        Create(other, OtherRecord, "deleted");
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);
        copy.Load();
        Replace(other, Record, 1, "second");
        Replace(other, Record, 2, "third");
        Assert.Equal(WriteOutcome.Deleted, other.DeleteDocument(OtherRecord, 1).Outcome);
        var listed = ListedUnder(other.Root);

        var load = LoadAMinuteLater(copy);

        Assert.Empty(load.Changed);
        Assert.Empty(load.Gone);
        Assert.Empty(load.Outside);
        Assert.Empty(load.Skipped);
        Assert.Equal(File.ReadAllBytes(other.DocumentPath(Record)), File.ReadAllBytes(PrivateCopyPath(Other, Record)));
        Assert.Equal(File.ReadAllBytes(other.TrashPath(OtherRecord)), File.ReadAllBytes(PrivateCopyPath(Other, OtherRecord, inTrash: true)));
        Assert.False(File.Exists(PrivateCopyPath(Other, OtherRecord)), "The deleted document is still in its folder in the copy.");
        Assert.DoesNotContain(listed, path => path.StartsWith(Path.Combine(other.Root, DataFolder.History), StringComparison.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(Path.Combine(PrivateCopyRoot(Other), DataFolder.History)), "The history of another private folder is in the copy.");
        Assert.Empty(copy.Trash());
    }

    [Fact]
    public void Load_AnEntryWithHistoryLeftFromWhenItWasTheUsersOwn_ListsNoHistoryOnceItIsAnothers()
    {
        // The history of a private document that the copy of its user has not taken yet, as its folder could not be listed.
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "first");
        var copy = Open(Me);
        copy.Load();
        Replace(mine, Record, 1, "second");
        var history = Path.GetDirectoryName(mine.HistoryPath(Record, 1));
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.List) && path == history)
            {
                throw new IOException("Made up.");
            }
        };
        Assert.Contains(new SkippedEntry(@"private\demo.user\history\records\8f3a1c", null, SkipReason.Unreadable), copy.Load().Skipped);
        _files.Before = null;

        // The same copy, opened by another user with a private folder of their own, who is given that of the first.
        Create(_share.CreatePrivate(Other), OtherRecord, "theirs");
        var theirs = Open(Other);
        theirs.CopyOthers([Me]);
        var listed = ListedUnder(mine.Root);

        var load = theirs.Load();

        Assert.Equal(OtherRecord.InPrivate(Other), Assert.Single(load.Changed).Key);
        Assert.Empty(load.Skipped);
        Assert.DoesNotContain(listed, path => path.StartsWith(Path.Combine(mine.Root, DataFolder.History), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Load_TheOtherPrivateFolders_HaveABudgetApartFromTheUsers_AndEachACapOfItsOwn()
    {
        WriteShared(Record, "shared");
        WriteShared(OtherRecord, "shared too");
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "other");
        Create(other, OtherRecord, "other too");
        Create(_share.CreatePrivate(Third), Record, "third");
        var copy = Open(Me, DataCopyTests.Limits(documents: 2, otherDocuments: 2, documentsPerOther: 1));
        copy.CopyOthers([Other, Third]);

        var load = copy.Load();

        Assert.Equal(Keys([Record, OtherRecord]), Keys(load.Changed));
        Assert.Equal(new SkippedEntry(@"private\demo.other\records", "b72e09.json", SkipReason.OverBudget), Assert.Single(load.Skipped));
        Assert.True(File.Exists(PrivateCopyPath(Other, Record)), "The first document of another private folder is not in the copy.");
        Assert.True(File.Exists(PrivateCopyPath(Third, Record)), "A user who filled their private folder kept another one out of the copy.");
    }

    [Fact]
    public void Load_AnotherPrivateFolder_TakesNoMoreBytesThanItsCap()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "other");
        Create(other, OtherRecord, "other too");
        var copy = Open(Me, DataCopyTests.Limits(bytesPerOther: new FileInfo(other.DocumentPath(Record)).Length));
        copy.CopyOthers([Other]);

        var load = copy.Load();

        Assert.Equal(new SkippedEntry(@"private\demo.other\records", "b72e09.json", SkipReason.OverBudget), Assert.Single(load.Skipped));
        Assert.True(File.Exists(PrivateCopyPath(Other, Record)), "The document within the cap is not in the copy.");
    }

    [Fact]
    public void Load_TheOtherPrivateFolders_TogetherTakeNoMoreThanTheirBudget()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "other");
        Create(other, OtherRecord, "other too");
        Create(_share.CreatePrivate(Third), Record, "third");
        var copy = Open(Me, DataCopyTests.Limits(otherDocuments: 2, documentsPerOther: 2));
        copy.CopyOthers([Third, Other]);

        var load = copy.Load();

        Assert.Equal(new SkippedEntry(@"private\demo.third\records", "8f3a1c.json", SkipReason.OverBudget), Assert.Single(load.Skipped));
        Assert.True(File.Exists(PrivateCopyPath(Other, OtherRecord)), "A document within the budget is not in the copy.");
    }

    [Fact]
    public void Load_AVersionThatNeedsRoomForItsLastGoodBytesToo_FindsItInTheBudgetOfAllTheOthers()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "first");
        Replace(other, Record, 1, "second");
        var third = _share.CreatePrivate(Third);
        Create(third, Record, "third");
        var full = new FileInfo(other.DocumentPath(Record)).Length + new FileInfo(third.DocumentPath(Record)).Length;
        var copy = OpenAt(Me, DataCopyTests.Limits(otherBytes: full));
        copy.CopyOthers([Other, Third]);
        copy.Load();

        // Version 1 again, no larger: the plan fits, and only the read finds that the last good bytes stay too.
        TestFolder.WriteAsKiler(other.DocumentPath(Record), DocumentFormat.New(Record, Content("x")));
        var load = LoadAMinuteLater(copy);

        Assert.Equal(new SkippedEntry(@"private\demo.other\records", "8f3a1c.json", SkipReason.OverBudget), Assert.Single(load.Skipped));
        Assert.Empty(load.Outside);
    }

    [Fact]
    public void Load_TheBriefLeftoversOfAnotherPrivateFolder_MakeRoomOnlyInThatFolder()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "other");
        var third = _share.CreatePrivate(Third);
        var copy = OpenAt(Me, DataCopyTests.Limits(otherDocuments: 1, documentsPerOther: 1));
        copy.CopyOthers([Other, Third]);
        copy.Load();
        File.Delete(other.DocumentPath(Record));
        LoadAMinuteLater(copy);
        Assert.True(File.Exists(PrivateKeptPath(Other, Record)), "The last good bytes of the gone document are not kept.");
        Create(third, Record, "third");

        var load = LoadAMinuteLater(copy);

        Assert.Equal(new SkippedEntry(@"private\demo.third\records", "8f3a1c.json", SkipReason.OverBudget), Assert.Single(load.Skipped));
        Assert.True(File.Exists(PrivateKeptPath(Other, Record)), "The leftover of one private folder made room for another.");

        // In its own folder, it makes room.
        Create(other, OtherRecord, "other again");
        load = LoadAMinuteLater(copy);

        Assert.Equal(new SkippedEntry(@"private\demo.third\records", "8f3a1c.json", SkipReason.OverBudget), Assert.Single(load.Skipped));
        Assert.True(File.Exists(PrivateCopyPath(Other, OtherRecord)), "The new document of the private folder is not in the copy.");
        Assert.False(File.Exists(PrivateKeptPath(Other, Record)), "The leftover did not make room in its own private folder.");
    }

    [Fact]
    public void Open_AnIndexWithMoreThanTwiceTheEntriesOfTheBudgetOfTheOthers_LeavesThemOut_AndKeepsTheUsersOwn()
    {
        WriteShared(Record, "shared");
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "one");
        Create(other, OtherRecord, "two");
        Create(other, Key, "key");
        var copy = Open(Me);
        copy.CopyOthers([Other]);
        copy.Load();

        var within = Open(Me, DataCopyTests.Limits(otherDocuments: 2));
        within.CopyOthers([Other]);
        _files.Reads = 0;
        within.Load();
        Assert.Equal(0, _files.Reads);

        // The other private folder is read again, as far as its budget goes, and nothing of the user's own.
        var over = Open(Me, DataCopyTests.Limits(otherDocuments: 1));
        Assert.Equal(Record, Assert.Single(over.Documents()).Key);
        over.CopyOthers([Other]);
        over.Load();
        Assert.Equal(1, _files.Reads);
    }

    [Fact]
    public void CopyOthers_MoreAccountsThanItTakes_AreRefused_AndChangeNothing()
    {
        Create(_share.CreatePrivate(Third), Record, "third");
        var copy = Open(Me);
        copy.CopyOthers([Third]);
        copy.CopyOthers([.. Enumerable.Range(0, DataCopy.MaxOthers).Select(i => $"demo.u{i:D3}"), Me]);
        copy.CopyOthers([Third]);

        Assert.Throws<ArgumentException>(() => copy.CopyOthers([.. Enumerable.Range(0, DataCopy.MaxOthers + 1).Select(i => $"demo.u{i:D3}")]));

        copy.Load();
        Assert.True(File.Exists(PrivateCopyPath(Third, Record)), "The accounts that the copy was given before have changed.");
    }

    [Fact]
    public void Open_DeletesTheOldFilesThatNoEntryNames_InEveryPrivateFolderOfTheCopy_AndKeepsTheYoungAndTheNamed()
    {
        Create(_share.CreatePrivate(Me), Record, "mine");
        Create(_share.CreatePrivate(Other), Record, "other");
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);
        copy.Load();
        string[] named = [PrivateCopyPath(Me, Record), PrivateCopyPath(Other, Record)];

        // In the private folder of the user, in that of another user whom the copy is given, and in one of an account never given.
        string[] strays =
        [
            PrivateCopyPath(Me, OtherRecord),
            PrivateCopyPath(Other, OtherRecord),
            PrivateCopyPath(Third, OtherRecord, inTrash: true),
            PrivateKeptPath(Third, OtherRecord),
        ];
        foreach (var stray in strays)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
            File.WriteAllText(stray, "made up");
        }

        // Young, it may be the write of another Kiler of the user, in another session.
        var young = PrivateKeptPath(Third, Record);
        File.WriteAllText(young, "made up");
        _clock.Advance(TimeSpan.FromMinutes(11));
        File.SetLastWriteTimeUtc(young, _clock.GetUtcNow().UtcDateTime);

        OpenAt(Me);

        Assert.All(strays, stray => Assert.False(File.Exists(stray), $"{stray} is still in the copy."));
        Assert.True(File.Exists(young), "A young file of a private folder is gone.");
        Assert.All(named, file => Assert.True(File.Exists(file), $"{file} has gone from the copy."));
    }

    [Fact]
    public void Load_WhilePrivateIsRenamedBetweenThePassesOfTheOthers_TakesNoPrivateFolderForGone()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "other");
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);
        copy.Load();
        var privateFolders = Path.Combine(_share.Root, DataFolder.Private);
        var elsewhere = Path.Combine(_test.Root, "elsewhere");
        bool? renamed = null;
        _files.Before = (step, path) =>
        {
            if (renamed is null && step == nameof(IFileSystem.Attributes) && path == other.Root)
            {
                try
                {
                    Directory.Move(privateFolders, elsewhere);
                    renamed = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    renamed = false;
                }
            }
        };

        var load = LoadAMinuteLater(copy);

        Assert.False(renamed, "private\\ could be renamed while the load listed the private folders of other users.");
        Assert.Empty(load.Skipped);
        Assert.True(File.Exists(PrivateCopyPath(Other, Record)), "The document of another private folder was taken for gone.");
    }

    [Fact]
    public void Load_WithoutPrivate_LoadsTheDataFolder_AndFindsNoOtherPrivateFolder()
    {
        WriteShared(Record, "shared");
        Directory.Delete(Path.Combine(_share.Root, DataFolder.Private));
        var copy = Open(Me);
        copy.CopyOthers([Other]);

        var load = copy.Load();

        Assert.Equal(Record, Assert.Single(load.Changed).Key);
        Assert.Empty(load.Skipped);
    }

    [Fact]
    public void Load_WhenPrivateIsALink_ReportsEachOtherPrivateFolder_AndFollowsNone()
    {
        WriteShared(Record, "shared");
        var privateFolders = Path.Combine(_share.Root, DataFolder.Private);
        TestFolder.SkipWithoutJunctions(privateFolders);
        var elsewhere = Path.Combine(_test.Root, "elsewhere");
        Directory.Move(privateFolders, elsewhere);
        Directory.CreateDirectory(Path.Combine(elsewhere, Other, DataFolder.Records));
        File.WriteAllBytes(Path.Combine(elsewhere, Other, DataFolder.Records, "8f3a1c.json"), DocumentFormat.New(Record, Content("elsewhere")));
        TestFolder.CreateJunction(privateFolders, elsewhere);
        var copy = Open(Me);
        copy.CopyOthers([Other]);

        var load = copy.Load();

        Assert.Equal(Record, Assert.Single(load.Changed).Key);
        Assert.Contains(new SkippedEntry(@"private\demo.other", null, SkipReason.Unreadable), load.Skipped);
        Assert.False(Directory.Exists(PrivateCopyRoot(Other)), "The copy followed the link.");
    }

    [Fact]
    public void Load_TheChangesOutsideKilerOfAPrivateFolderThatIsNoLongerGiven_AreReportedOnlyOnceItIsGivenAgain()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "other");
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);
        copy.Load();
        File.Delete(other.DocumentPath(Record));
        LoadAMinuteLater(copy);
        copy.CopyOthers([]);
        _clock.Advance(InPlaceEdits.SettleTime);

        Assert.Empty(copy.Load().Outside);

        copy.CopyOthers([Other]);
        Assert.Equal(new OutsideChange(TheirRecord, InTrash: false, OutsideReason.Gone, LastGood: 1), Assert.Single(copy.Load().Outside));
    }

    [Fact]
    public void Load_AFileOfAnotherPrivateFolderOverTheSizeLimit_IsDamaged_AndNotGiven()
    {
        var other = _share.CreatePrivate(Other);
        TestFolder.WriteAsKiler(other.DocumentPath(Record), new byte[DocumentFormat.MaxSize + 1]);
        var copy = Open(Me);
        copy.CopyOthers([Other]);

        var load = copy.Load();

        Assert.Empty(load.Changed);
        Assert.Equal(new OutsideChange(TheirRecord, InTrash: false, OutsideReason.Damaged, LastGood: null), Assert.Single(load.Outside));
    }

    [Theory]
    [InlineData(1, 1, null)]
    [InlineData(null, null, 2)]
    public void Load_OverItsBudgets_AsTheyAreSmallerAtTheStart_TakesEachBackWithItsOwnLeftovers(int? documents, int? documentsPerOther, int? otherDocuments)
    {
        WriteShared(Record, "shared");
        WriteShared(OtherRecord, "shared too");
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "other");
        Create(other, OtherRecord, "other too");
        var third = _share.CreatePrivate(Third);
        Create(third, Record, "third");
        var copy = OpenAt(Me);
        copy.CopyOthers([Other, Third]);
        copy.Load();

        // The leftover of the third user went first, then those of the user, then those of the other.
        File.Delete(third.DocumentPath(Record));
        LoadAMinuteLater(copy);
        _clock.Advance(TimeSpan.FromMinutes(1));
        File.Delete(_share.DocumentPath(Record));
        File.Delete(_share.DocumentPath(OtherRecord));
        copy.Load();
        _clock.Advance(TimeSpan.FromMinutes(1));
        File.Delete(other.DocumentPath(Record));
        File.Delete(other.DocumentPath(OtherRecord));
        copy.Load();

        // Either the budget of the user and of each private folder, or only that of all the private folders, is smaller.
        var again = OpenAt(Me, DataCopyTests.Limits(documents: documents, documentsPerOther: documentsPerOther, otherDocuments: otherDocuments));
        again.CopyOthers([Other, Third]);
        again.Load();

        Assert.Equal(documents ?? 2, ((string[])[SharedKeptPath(Record), SharedKeptPath(OtherRecord)]).Count(File.Exists));
        Assert.Single((string[])[PrivateKeptPath(Other, Record), PrivateKeptPath(Other, OtherRecord)], File.Exists);
        Assert.True(File.Exists(PrivateKeptPath(Third, Record)), "A budget made room with the leftover of another.");
    }

    [Fact]
    public void Writes_ThroughTheCopy_HaveTheBudgetOfTheUsersOwn_WhateverTheOtherPrivateFoldersTake()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "one");
        Create(other, OtherRecord, "two");
        var copy = Open(Me, DataCopyTests.Limits(documents: 1));
        copy.CopyOthers([Other]);
        copy.Load();

        Assert.Equal(WriteOutcome.Written, copy.Create(Record, Content("shared")).Outcome);

        Assert.Equal(Record, Assert.Single(copy.Documents()).Key);
    }

    [Fact]
    public void CopyOthers_WithoutAnAccount_LeavesWhatTheCopyHasOfItAsItIs_UntilItIsGivenAgain()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "other");
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);
        copy.Load();
        var good = File.ReadAllBytes(other.DocumentPath(Record));
        copy.CopyOthers([]);
        File.Delete(other.DocumentPath(Record));
        _clock.Advance(TimeSpan.FromMinutes(11));

        var load = copy.Load();

        Assert.Empty(load.Outside);
        Assert.Empty(load.Skipped);
        Assert.Equal(good, File.ReadAllBytes(PrivateCopyPath(Other, Record)));

        // Also after a start, while it is not given; the next load that is given it finds the delete.
        var again = OpenAt(Me);
        again.Load();
        Assert.Equal(good, File.ReadAllBytes(PrivateCopyPath(Other, Record)));
        again.CopyOthers([Other]);
        again.Load();
        _clock.Advance(InPlaceEdits.SettleTime);
        Assert.Equal(new OutsideChange(TheirRecord, InTrash: false, OutsideReason.Gone, LastGood: 1), Assert.Single(again.Load().Outside));
        Assert.Equal(good, File.ReadAllBytes(PrivateKeptPath(Other, Record)));
    }

    [Fact]
    public void Open_AnIndexWithTheOtherPrivateFolders_IsTakenWithinTheirBudgetToo()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "one");
        Create(other, OtherRecord, "two");
        Create(other, Key, "key");
        var limits = DataCopyTests.Limits(documents: 1, otherDocuments: 3, documentsPerOther: 3);
        var copy = OpenAt(Me, limits);
        copy.CopyOthers([Other]);
        copy.Load();
        _clock.Advance(TimeSpan.FromMinutes(11));

        var again = OpenAt(Me, limits);
        again.CopyOthers([Other]);
        _files.Reads = 0;
        var load = again.Load();

        Assert.Equal(0, _files.Reads);
        Assert.Empty(load.Skipped);
        Assert.True(File.Exists(PrivateCopyPath(Other, Key)), "The copy of another private folder was lost at the start.");
    }

    [Fact]
    public void CopyOthers_TheAccountOfTheUser_StaysTheirOwn_AndIsListedOnce()
    {
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "mine");
        var copy = Open(Me);
        copy.CopyOthers([Me]);
        var listed = ListedUnder(mine.Root);

        var load = copy.Load();

        Assert.Equal(Record.InPrivate(Me), Assert.Single(load.Changed).Key);
        Assert.Equal(Record.InPrivate(Me), Assert.Single(copy.Documents()).Key);
        Assert.Equal(listed.Distinct(StringComparer.OrdinalIgnoreCase).Count(), listed.Count);
    }

    [Theory]
    [InlineData("Demo.Other")]
    [InlineData("..")]
    [InlineData(@"a\b")]
    [InlineData("demo.other.tmp")]
    public void CopyOthers_AnAccountThatFailsTheNameCheck_IsRefused_AndChangesNothing(string account)
    {
        Create(_share.CreatePrivate(Other), Record, "other");
        Create(_share.CreatePrivate(Third), Record, "third");
        var copy = Open(Me);
        copy.CopyOthers([Third]);

        Assert.Throws<ArgumentException>(() => copy.CopyOthers([Other, account]));

        copy.Load();
        Assert.True(File.Exists(PrivateCopyPath(Third, Record)), "The accounts that the copy was given before have changed.");
        Assert.False(Directory.Exists(PrivateCopyRoot(Other)), "An account of a refused list is in the copy.");
    }

    [Fact]
    public void Writes_ThroughTheCopy_ToAnotherPrivateFolderThatItCopies_AreRefused()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "other");
        var copy = Open(Me);
        copy.CopyOthers([Other]);
        copy.Load();

        Assert.Throws<ArgumentException>(() => copy.Create(OtherRecord.InPrivate(Other), Content("made up")));
        Assert.Throws<ArgumentException>(() => copy.Replace(TheirRecord, 1, Content("made up")));
        Assert.Throws<ArgumentException>(() => copy.Delete(TheirRecord, 1));
        Assert.Throws<ArgumentException>(() => copy.RestoreFromTrash(TheirRecord));

        Assert.False(File.Exists(other.DocumentPath(OtherRecord)), "A create went to the private folder of another user.");
        Assert.False(File.Exists(other.HistoryPath(Record, 1)), "A replace or a delete went to the private folder of another user.");
    }

    [Fact]
    public void Load_AnotherPrivateFolderThatIsNotThere_GivesNothing_AndOneMadeLater_IsCopied()
    {
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);

        var first = copy.Load();

        Assert.Empty(first.Skipped);
        Assert.False(Directory.Exists(PrivateCopyRoot(Other)), "A private folder that is not there is in the copy.");
        Create(_share.CreatePrivate(Other), Record, "other");
        Assert.Empty(LoadAMinuteLater(copy).Skipped);
        Assert.True(File.Exists(PrivateCopyPath(Other, Record)), "A private folder made later is not in the copy.");
    }

    [Fact]
    public void Load_AnotherPrivateFolderThatIsALink_IsReported_AndNotFollowed_AndItsDocumentsStay()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "other");
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);
        copy.Load();
        var elsewhere = Path.Combine(_test.Root, "elsewhere");
        TestFolder.SkipWithoutJunctions(other.Root);
        Directory.Move(other.Root, elsewhere);
        File.WriteAllBytes(Path.Combine(elsewhere, DataFolder.Records, "b72e09.json"), DocumentFormat.New(OtherRecord, Content("elsewhere")));
        TestFolder.CreateJunction(other.Root, elsewhere);
        _files.Reads = 0;

        var load = LoadAMinuteLater(copy);

        Assert.Equal(0, _files.Reads);
        Assert.Equal(new SkippedEntry(@"private\demo.other", null, SkipReason.Unreadable), Assert.Single(load.Skipped));
        _clock.Advance(InPlaceEdits.SettleTime);
        Assert.Empty(copy.Load().Outside);
        Assert.True(File.Exists(PrivateCopyPath(Other, Record)), "The documents of the private folder have gone from the copy.");
        Assert.False(File.Exists(PrivateCopyPath(Other, OtherRecord)), "The copy followed the link.");
    }

    /// <summary>
    /// An admin's load takes the private folders of other users once a minute, as they are only for a restore (decided in M2.8c,
    /// by the user): the first load, the first after <see cref="DataCopy.OthersInterval"/>, and the next load after each call of
    /// <see cref="DataCopy.CopyOthers"/>. The documents that a load takes new in one place give no load more.
    /// </summary>
    [Fact]
    public void Load_ThePrivateFoldersOfOtherUsers_AreListedOnceAMinute_AndAtTheNextLoadAfterCopyOthers()
    {
        Create(_share.CreatePrivate(Other), Record, "other");
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);

        Assert.True(ListsOther(copy), "The first load did not take the private folder of the other user.");

        Assert.Equal([false, false, false, false, false, true, false], ListsOtherAtThePasses(copy, 7));
        copy.CopyOthers([Other]);
        Assert.True(ListsOther(copy), "The load after CopyOthers did not take the private folder of the other user.");
        Assert.Equal([false], ListsOtherAtThePasses(copy, 1));
    }

    [Fact]
    public void Load_ThePrivateFoldersOfOtherUsers_AreListedAtOnceWhenTheClockIsPutBack()
    {
        Create(_share.CreatePrivate(Other), Record, "other");
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);
        copy.Load();
        _clock.AdjustTime(_clock.GetUtcNow() - TimeSpan.FromMinutes(1));

        Assert.True(ListsOther(copy), "A load after the clock was put back did not take the private folder of the other user.");

        Assert.Equal([false], ListsOtherAtThePasses(copy, 1));
    }

    /// <summary>A load that throws before it has taken every private folder of other users leaves them all for the next load, not for a minute later.</summary>
    [Fact]
    public void Load_ThatThrowsInThePrivateFoldersOfOtherUsers_TakesThemAllAtTheNextLoad()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "other");
        var third = _share.CreatePrivate(Third);
        Create(third, Record, "third");
        var copy = OpenAt(Me);
        copy.CopyOthers([Other, Third]);
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.List) && path.StartsWith(third.Root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The test failed the load.");
            }
        };
        Assert.Throws<InvalidOperationException>(() => copy.Load());
        _clock.Advance(DataWatch.Interval);
        var listed = ListedUnder(Path.Combine(_share.Root, DataFolder.Private));

        copy.Load();

        Assert.Contains(other.Root, listed, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(third.Root, listed, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A move to the trash in the private folder of another user that a listing of the trash shows a load late, through the SMB
    /// cache, is followed, and is no document gone (the M2.8b note): the next load takes the private folder again, as it has a
    /// document that went less than <see cref="InPlaceEdits.SettleTime"/> ago and is not reported (decided in M2.8c, by the
    /// user), and once the move is followed the folder waits for its minute.
    /// </summary>
    [Fact]
    public void Load_AMoveInAnotherPrivateFolderThatTheTrashListsLate_IsFollowedAtTheNextLoad()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "other");
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);
        copy.Load();
        var bytes = File.ReadAllBytes(other.DocumentPath(Record));
        Assert.Equal(WriteOutcome.Deleted, other.DeleteDocument(Record, 1).Outcome);
        var hidden = Path.Combine(_test.Root, "hidden.json");
        File.Move(other.TrashPath(Record), hidden);
        _clock.Advance(DataCopy.OthersInterval);
        Assert.True(ListsOther(copy), "The load a minute later did not take the private folder of the other user.");
        File.Move(hidden, other.TrashPath(Record));
        var outside = new List<OutsideChange>();

        Assert.Equal([true, false, false, false, false], ListsOtherAtThePasses(copy, 5, outside));

        Assert.Empty(outside);
        Assert.False(File.Exists(PrivateKeptPath(Other, Record)), "The moved document is kept apart.");
        Assert.Equal(bytes, File.ReadAllBytes(PrivateCopyPath(Other, Record, inTrash: true)));
    }

    /// <summary>
    /// The other way: a load lists the document in both places, as the listing of its folder is late, and the next load takes
    /// the private folder again, as a document there in both places was taken less than <see cref="InPlaceEdits.SettleTime"/>
    /// ago; once one of the two has gone, the folder waits for its minute (decided in M2.8c, by the user).
    /// </summary>
    [Fact]
    public void Load_AMoveInAnotherPrivateFolderThatItsFolderListsLate_IsFollowedAtTheNextLoad()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "other");
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);
        copy.Load();
        var bytes = File.ReadAllBytes(other.DocumentPath(Record));
        Directory.CreateDirectory(Path.GetDirectoryName(other.TrashPath(Record))!);
        TestFolder.WriteAsKiler(other.TrashPath(Record), bytes);
        _clock.Advance(DataCopy.OthersInterval);
        Assert.True(ListsOther(copy), "The load a minute later did not take the private folder of the other user.");
        File.Delete(other.DocumentPath(Record));
        var outside = new List<OutsideChange>();

        Assert.Equal([true, false, false, false, false, true, false, false, false], ListsOtherAtThePasses(copy, 9, outside));

        Assert.Empty(outside);
        Assert.False(File.Exists(PrivateKeptPath(Other, Record)), "The moved document is kept apart.");
        Assert.Equal(bytes, File.ReadAllBytes(PrivateCopyPath(Other, Record, inTrash: true)));
    }

    /// <summary>A document of another private folder deleted outside Kiler keeps its folder taken by each load until it is reported, and no longer.</summary>
    [Fact]
    public void Load_ADocumentDeletedOutsideKilerInAnotherPrivateFolder_KeepsItsFolderTakenUntilItIsReported()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "other");
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);
        copy.Load();
        File.Delete(other.DocumentPath(Record));
        _clock.Advance(DataCopy.OthersInterval);
        Assert.True(ListsOther(copy), "The load a minute later did not take the private folder of the other user.");
        var outside = new List<OutsideChange>();

        Assert.Equal([true, true, false, false], ListsOtherAtThePasses(copy, 4, outside));

        Assert.Equal(new OutsideChange(TheirRecord, InTrash: false, OutsideReason.Gone, LastGood: 1), Assert.Single(outside));
    }

    /// <summary>A load in between takes only the private folders of other users that wait for a listing, not all of them.</summary>
    [Fact]
    public void Load_InBetween_TakesOnlyThePrivateFolderOfAnotherUserThatWaitsForAListing()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "other");
        var third = _share.CreatePrivate(Third);
        Create(third, Record, "third");
        var copy = OpenAt(Me);
        copy.CopyOthers([Other, Third]);
        copy.Load();
        File.Delete(other.DocumentPath(Record));
        LoadAMinuteLater(copy);
        _clock.Advance(DataWatch.Interval);
        var listed = ListedUnder(Path.Combine(_share.Root, DataFolder.Private));

        copy.Load();

        Assert.Contains(other.Root, listed, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(listed, path => path.StartsWith(third.Root, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A load that takes no private folder of another user does not hold <c>private\</c> for them.</summary>
    [Fact]
    public void Load_ThatTakesNoPrivateFolderOfOtherUsers_DoesNotHoldPrivate()
    {
        Create(_share.CreatePrivate(Other), Record, "other");
        var copy = DataCopy.Open(_share, _copy, _clock, account: null);
        copy.CopyOthers([Other]);
        var privateFolders = Path.Combine(_share.Root, DataFolder.Private);
        var holds = 0;
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.HoldFolder) && path.Equals(privateFolders, StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref holds);
            }
        };
        copy.Load();
        Assert.True(Volatile.Read(ref holds) > 0, "The load that took the private folder of the other user did not hold private.");
        Volatile.Write(ref holds, 0);
        _clock.Advance(DataWatch.Interval);

        copy.Load();

        Assert.Equal(0, Volatile.Read(ref holds));
    }

    /// <summary>
    /// A write of another Kiler can look edited in place for about 10 s through the SMB cache (M2.3), and a load that sees it
    /// so takes the private folder again at the next load, until the edit is reported or the document is read again, so the
    /// write is not reported for a restore (decided in M2.8c, with the moves).
    /// </summary>
    [Fact]
    public void Load_AWriteInAnotherPrivateFolderThatLooksEditedForAMoment_IsReadAgainAtTheNextLoad_AndNotReported()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "first");
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);
        copy.Load();
        Replace(other, Record, 1, "second");
        var path = other.DocumentPath(Record);
        var written = File.GetLastWriteTimeUtc(path);
        File.SetLastWriteTimeUtc(path, written.AddSeconds(1));
        _clock.Advance(DataCopy.OthersInterval);
        Assert.True(ListsOther(copy), "The load a minute later did not take the private folder of the other user.");
        File.SetLastWriteTimeUtc(path, written);
        var outside = new List<OutsideChange>();

        Assert.Equal([true, false, false, false, false], ListsOtherAtThePasses(copy, 5, outside));

        Assert.Empty(outside);
    }

    [Fact]
    public void Load_ADocumentEditedInPlaceInAnotherPrivateFolder_KeepsItsFolderTakenUntilItIsReported()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "first");
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);
        copy.Load();
        TestFolder.EditInPlace(other.DocumentPath(Record), DocumentFormat.Next(Record, 1, Content("edited")));
        _clock.Advance(DataCopy.OthersInterval);
        Assert.True(ListsOther(copy), "The load a minute later did not take the private folder of the other user.");
        var outside = new List<OutsideChange>();

        Assert.Equal([true, true, true, false], ListsOtherAtThePasses(copy, 4, outside));

        Assert.Equal(new OutsideChange(TheirRecord, InTrash: false, OutsideReason.EditedInPlace, LastGood: 1), Assert.Single(outside));
    }

    /// <summary>
    /// The skipped entries of each load are the state of the share as the copy knows it (M2.8), so the loads that do not take
    /// the private folder of another user report what its last pass skipped, until the next pass (M2.8c); one that the copy is
    /// no longer given reports nothing.
    /// </summary>
    [Fact]
    public void Load_TheSkippedEntriesOfAnotherPrivateFolder_AreReportedByEveryLoad_AsItsLastPassLeftThem()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "other");
        var bad = Path.Combine(other.Root, DataFolder.Records, "Bad.json");
        TestFolder.WriteAsKiler(bad, DocumentFormat.New(OtherRecord, Content("bad")));
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);
        var skipped = Assert.Single(copy.Load().Skipped);
        Assert.Equal(SkipReason.BadName, skipped.Reason);
        File.Delete(bad);

        for (var pass = 1; pass < 6; pass++)
        {
            _clock.Advance(DataWatch.Interval);
            Assert.Equal([skipped], copy.Load().Skipped);
        }

        _clock.Advance(DataWatch.Interval);
        Assert.Empty(copy.Load().Skipped);
        TestFolder.WriteAsKiler(bad, DocumentFormat.New(OtherRecord, Content("bad")));
        Assert.Equal([skipped], LoadAMinuteLater(copy).Skipped);
        copy.CopyOthers([]);
        _clock.Advance(DataWatch.Interval);
        Assert.Empty(copy.Load().Skipped);
    }

    /// <summary>What the private folders of other users keep for the loads that do not take them is no more than one load gives, as anyone can fill a folder with links.</summary>
    [Fact]
    public void Load_TheSkippedEntriesThatThePrivateFoldersOfOtherUsersKeep_AreNoMoreThanALoadGives()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "other");

        // In both places, so the next load takes the folder again.
        Directory.CreateDirectory(Path.GetDirectoryName(other.TrashPath(Record))!);
        TestFolder.WriteAsKiler(other.TrashPath(Record), File.ReadAllBytes(other.DocumentPath(Record)));
        var third = _share.CreatePrivate(Third);
        TestFolder.WriteAsKiler(Path.Combine(third.Root, DataFolder.Records, "B.json"), DocumentFormat.New(OtherRecord, Content("bad")));
        var copy = OpenAt(Me, DataCopyTests.Limits(reports: 2));
        copy.CopyOthers([Other, Third]);
        Assert.Single(copy.Load().Skipped);
        foreach (var name in (string[])["A.json", "C.json"])
        {
            TestFolder.WriteAsKiler(Path.Combine(other.Root, DataFolder.Records, name), DocumentFormat.New(OtherRecord, Content("bad")));
        }

        _clock.Advance(DataWatch.Interval);

        var load = copy.Load();

        Assert.Equal(2, load.Skipped.Count);
        Assert.Equal(1, load.MoreSkipped);
        Assert.Equal(2, copy.OthersSkipsKept);

        // Once the folder waits for no listing, a load takes neither, and reports what each kept, with the rest counted.
        _clock.Advance(InPlaceEdits.SettleTime - DataWatch.Interval);
        var after = copy.Load();
        Assert.Equal(2, after.Skipped.Count);
        Assert.Equal(1, after.MoreSkipped);
    }

    /// <summary>Accepting the private folder of another user has the next load take the folder at the name, as for the user's own (M2.7d), not a minute later.</summary>
    [Fact]
    public void AcceptPrivateFolder_OfAnotherUser_IsTakenAtTheNextLoad()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "other");
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);
        copy.Load();
        Directory.Move(other.Root, Path.Combine(_test.Root, "elsewhere"));
        Create(_share.CreatePrivate(Other), OtherRecord, "at the name");
        Assert.Equal(new SkippedEntry(@"private\demo.other", null, SkipReason.ForeignFolder), Assert.Single(LoadAMinuteLater(copy).Skipped));

        copy.AcceptPrivateFolder(Other);

        _clock.Advance(DataWatch.Interval);
        Assert.Empty(copy.Load().Skipped);
        Assert.True(File.Exists(PrivateCopyPath(Other, OtherRecord)), "The folder at the name was not taken at the next load.");
    }

    private static JsonObject Content(string name) => new() { ["name"] = name };

    private static string[] Keys(IEnumerable<DocumentKey> keys) => [.. keys.Select(key => key.ToString()).Order(StringComparer.Ordinal)];

    private static string[] Keys(IEnumerable<LoadedDocument> documents) => Keys(documents.Select(document => document.Key));

    private static void Create(DataFolder root, DocumentKey key, string name) => Assert.Equal(WriteOutcome.Written, root.CreateDocument(key, Content(name)).Outcome);

    private static void Replace(DataFolder root, DocumentKey key, int version, string name) =>
        Assert.Equal(WriteOutcome.Written, root.ReplaceDocument(key, version, Content(name)).Outcome);

    private DataCopy Open(string account) => DataCopy.Open(_share, _copy, TimeProvider.System, account);

    private DataCopy Open(string account, CopyLimits limits) => DataCopy.Open(_share, _copy, TimeProvider.System, limits, account);

    /// <summary>Opens the copy on <see cref="_clock"/>.</summary>
    private DataCopy OpenAt(string account, CopyLimits? limits = null) => DataCopy.Open(_share, _copy, _clock, limits ?? CopyLimits.Default, account);

    /// <summary>The folders under <paramref name="root"/> that the share lists from now on, filled as the steps come.</summary>
    private List<string> ListedUnder(string root)
    {
        var listed = new List<string>();
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.List) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                lock (listed)
                {
                    listed.Add(path);
                }
            }
        };
        return listed;
    }

    /// <summary>Loads a copy on <see cref="_clock"/> a minute later, when the load takes the private folders of other users again (<see cref="DataCopy.OthersInterval"/>, M2.8c).</summary>
    private LoadResult LoadAMinuteLater(DataCopy copy)
    {
        _clock.Advance(DataCopy.OthersInterval);
        return copy.Load();
    }

    /// <summary>Loads the copy once, and gives whether the load listed the private folder of <see cref="Other"/>; collects its changes outside Kiler into <paramref name="outside"/>.</summary>
    private bool ListsOther(DataCopy copy, List<OutsideChange>? outside = null)
    {
        var listed = ListedUnder(Path.Combine(_share.Root, DataFolder.Private, Other));
        try
        {
            var load = copy.Load();
            outside?.AddRange(load.Outside);
        }
        finally
        {
            _files.Before = null;
        }

        return listed.Count > 0;
    }

    /// <summary>Loads the copy at each of <paramref name="passes"/> passes of the watch, <see cref="DataWatch.Interval"/> apart, as <see cref="ListsOther"/> does.</summary>
    private bool[] ListsOtherAtThePasses(DataCopy copy, int passes, List<OutsideChange>? outside = null)
    {
        var lists = new bool[passes];
        for (var pass = 0; pass < passes; pass++)
        {
            _clock.Advance(DataWatch.Interval);
            lists[pass] = ListsOther(copy, outside);
        }

        return lists;
    }

    /// <summary>A document file of the data folder, as a write of Kiler leaves it, at version 1.</summary>
    private void WriteShared(DocumentKey key, string name) => TestFolder.WriteAsKiler(_share.DocumentPath(key), DocumentFormat.New(key, Content(name)));

    /// <summary>Where the local copy keeps a private folder: <c>data\private\&lt;account&gt;\</c>, as the share has it.</summary>
    private string PrivateCopyRoot(string account) => Path.Combine(_copy, "data", DataFolder.Private, account);

    private string PrivateCopyPath(string account, DocumentKey key, bool inTrash = false) =>
        Path.Combine(PrivateCopyRoot(account), inTrash ? DataFolder.Trash : "", key.Folder ?? "", key.Id + ".json");

    private string PrivateKeptPath(string account, DocumentKey key) => Path.Combine(_copy, "kept", DataFolder.Private, account, key.Folder ?? "", key.Id + ".json");

    private string SharedKeptPath(DocumentKey key) => Path.Combine(_copy, "kept", key.Folder ?? "", key.Id + ".json");
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Storage.Tests;

/// <summary>
/// The history of a document as the local copy gives it (M5.11, <see cref="DataCopy.History"/>): the old versions that the
/// copy keeps in its history (M2.7a), read from the copy and never from the share, for the history of a record.
/// </summary>
public sealed class DataCopyHistoryTests : IDisposable
{
    private const string Me = "demo.user";

    private static readonly DocumentKey Record = DocumentKey.InFolder(DataFolder.Records, "8f3a1c");
    private static readonly DocumentKey OtherRecord = DocumentKey.InFolder(DataFolder.Records, "b72e09");

    private readonly TestFolder _test = new();
    private readonly TestFileSystem _files = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly DataFolder _share;
    private readonly string _copy;

    public DataCopyHistoryTests()
    {
        _share = DataFolder.Create(Path.Combine(_test.Root, "data"), _files);
        _copy = Directory.CreateDirectory(Path.Combine(_test.Root, "copy")).FullName;
    }

    public void Dispose() => _test.Dispose();

    /// <summary>
    /// The slots of this Kiler's own writes and those that a load took after a write of another Kiler, the newest first,
    /// without the current version; after a restart too, while the share cannot be touched.
    /// </summary>
    [Fact]
    public void History_GivesTheOldVersionsThatTheCopyKeeps_TheNewestFirst_WithoutTheShare()
    {
        var copy = OpenAt();
        Assert.Equal(WriteOutcome.Written, copy.Create(Record, Content("v1")).Outcome);
        Assert.Equal(WriteOutcome.Written, copy.Replace(Record, 1, Content("v2")).Outcome);
        Assert.Equal(WriteOutcome.Written, copy.Replace(Record, 2, Content("v3")).Outcome);
        Assert.Equal(WriteOutcome.Written, copy.Create(OtherRecord, Content("other")).Outcome);
        Assert.Equal(WriteOutcome.Written, copy.Replace(OtherRecord, 1, Content("other v2")).Outcome);
        Replace(Record, 3, "v4");
        Replace(Record, 4, "v5");
        copy.Load();
        _files.Before = (step, path) => throw new IOException("The share was touched.");

        foreach (var history in (IReadOnlyList<Document>[])[copy.History(Record), OpenAt().History(Record)])
        {
            Assert.Equal([4, 3, 2, 1], Versions(history));
            Assert.Equal(["v4", "v3", "v2", "v1"], history.Select(document => (string?)document.Content["name"]).ToArray());
            Assert.All(history, document => Assert.Equal(Record, document.Key));
        }
    }

    [Fact]
    public void History_OfADocumentThatWasNeverChanged_IsEmpty()
    {
        var copy = OpenAt();
        Assert.Equal(WriteOutcome.Written, copy.Create(Record, Content("v1")).Outcome);

        Assert.Empty(copy.History(Record));
        Assert.Empty(copy.History(OtherRecord));
    }

    /// <summary>
    /// A slot whose file in the copy is not the one its entry names is left out, as a crash between the file and the index, or
    /// a hand in the profile, can leave it so; the others stay. One that cannot be read now comes back once it can.
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("other bytes")]
    [InlineData("held")]
    [InlineData("another version")]
    [InlineData("damaged")]
    public void History_ASlotWhoseFileIsNotTheOneOfItsEntry_IsLeftOut(string how)
    {
        var copy = OpenAt();
        Assert.Equal(WriteOutcome.Written, copy.Create(Record, Content("v1")).Outcome);
        Assert.Equal(WriteOutcome.Written, copy.Replace(Record, 1, Content("v2")).Outcome);
        Assert.Equal(WriteOutcome.Written, copy.Replace(Record, 2, Content("v3")).Outcome);
        var path = SlotCopyPath(Record, 2);
        FileStream? held = null;
        switch (how)
        {
            case "missing":
                File.Delete(path);
                break;
            case "other bytes":
                File.WriteAllBytes(path, DocumentFormat.Next(Record, 1, Content("other")));
                break;
            case "held":
                held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                break;
            case "another version":
                Swap(path, File.ReadAllBytes(SlotCopyPath(Record, 1)));
                copy = OpenAt();
                break;
            case "damaged":
                Swap(path, Encoding.UTF8.GetBytes("{ \"id\": \"8f3a1c\", \"version\": 2, "));
                copy = OpenAt();
                break;
        }

        try
        {
            Assert.Equal([1], Versions(copy.History(Record)));
        }
        finally
        {
            held?.Dispose();
        }

        if (held is not null)
        {
            Assert.Equal([2, 1], Versions(copy.History(Record)));
        }
    }

    /// <summary>
    /// A private record and a shared one of one id are two keys (M2.7b), so each has its own history, though the read of a slot
    /// of the other would pass the check of its id.
    /// </summary>
    [Fact]
    public void History_OfADocument_GivesNoSlotOfThePrivateDocumentOfItsId()
    {
        var copy = DataCopy.Open(_share, _copy, _clock, Me);
        copy.CreatePrivate();
        var mine = Record.InPrivate(Me);
        Assert.Equal(WriteOutcome.Written, copy.Create(Record, Content("shared v1")).Outcome);
        Assert.Equal(WriteOutcome.Written, copy.Replace(Record, 1, Content("shared v2")).Outcome);
        Assert.Equal(WriteOutcome.Written, copy.Create(mine, Content("private v1")).Outcome);
        Assert.Equal(WriteOutcome.Written, copy.Replace(mine, 1, Content("private v2")).Outcome);
        Assert.Equal(WriteOutcome.Written, copy.Replace(mine, 2, Content("private v3")).Outcome);

        Assert.Equal(["shared v1"], copy.History(Record).Select(document => (string?)document.Content["name"]).ToArray());
        Assert.Equal(["private v2", "private v1"], copy.History(mine).Select(document => (string?)document.Content["name"]).ToArray());
    }

    /// <summary>The slots that the copy keeps for a document of the same id in the trash are not the history of the document in its folder (M2.7a).</summary>
    [Fact]
    public void History_GivesNoSlotOfTheTrash()
    {
        Create(Record, "v1");
        var copy = OpenAt();
        copy.Load();
        Assert.Equal(WriteOutcome.Deleted, _share.DeleteDocument(Record, 1).Outcome);
        copy.Load();
        TestFolder.WriteAsKiler(_share.HistoryPath(Record, 2), DocumentFormat.Next(Record, 1, Content("v2")));
        TestFolder.WriteAsKiler(_share.TrashPath(Record), DocumentFormat.Next(Record, 2, Content("v3")));

        copy.Load();

        Assert.True(File.Exists(Path.Combine(_copy, "data", "trash", "history", "records", "8f3a1c", "v2.json")), "The copy took no slot of the trash.");
        Assert.Empty(copy.History(Record));
    }

    /// <summary>The copy gives no document of the private folder of another user (M2.7c), so a key of one is a fault of the caller.</summary>
    [Fact]
    public void History_OfAKeyOfAnotherPrivateFolder_IsRefused()
    {
        var copy = DataCopy.Open(_share, _copy, _clock, Me);

        Assert.Throws<ArgumentException>(() => copy.History(Record.InPrivate("demo.other")));
        Assert.Empty(copy.History(Record.InPrivate(Me)));
        Assert.Throws<ArgumentException>(() => OpenAt().History(Record.InPrivate(Me)));
    }

    private static JsonObject Content(string name) => new() { ["name"] = name };

    private static int[] Versions(IEnumerable<Document> history) => [.. history.Select(document => document.Version)];

    private DataCopy OpenAt() => DataCopy.Open(_share, _copy, _clock);

    /// <summary>A document on the share, as a write of another Kiler makes it.</summary>
    private void Create(DocumentKey key, string name) => Assert.Equal(WriteOutcome.Written, _share.CreateDocument(key, Content(name)).Outcome);

    /// <summary>A new version of a document on the share, as a write of another Kiler makes it, with the old one in its history slot.</summary>
    private void Replace(DocumentKey key, int version, string name) => Assert.Equal(WriteOutcome.Written, _share.ReplaceDocument(key, version, Content(name)).Outcome);

    /// <summary>Puts <paramref name="bytes"/> into the slot file at <paramref name="path"/> with an index whose entry names them, as a hand in the profile could.</summary>
    private void Swap(string path, byte[] bytes)
    {
        File.WriteAllBytes(path, bytes);
        var indexPath = Path.Combine(_copy, "index.json");
        var index = JsonNode.Parse(File.ReadAllBytes(indexPath))!;
        var slot = index["history"]!.AsArray().Single(entry => (int)entry!["version"]! == 2 && (string?)entry["id"] == Record.Id)!;
        slot["hash"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        slot["length"] = bytes.Length;
        File.WriteAllText(indexPath, index.ToJsonString());
    }

    /// <summary>A history slot in the copy: <c>data\history\records\8f3a1c\v1.json</c> (M2.7a).</summary>
    private string SlotCopyPath(DocumentKey key, int version) => Path.Combine(_copy, "data", "history", key.Folder ?? "", key.Id, $"v{version}.json");
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Storage.Tests;

/// <summary>
/// The last good bytes that the local copy keeps apart for a change outside Kiler of a document in its folder, as a
/// document (M4.16a, <see cref="DataCopy.KeptApart"/>): read from the copy and never from the share, so Kiler.Core can take
/// the newest version that an admin sealed also when the share's file is not a version that Kiler made.
/// </summary>
public sealed class DataCopyKeptApartTests : IDisposable
{
    private const string Me = "demo.user";

    private static readonly DocumentKey Record = DocumentKey.InFolder(DataFolder.Records, "8f3a1c");
    private static readonly DocumentKey OtherRecord = DocumentKey.InFolder(DataFolder.Records, "b72e09");
    private static readonly DocumentKey Settings = DocumentKey.AtRoot("settings");

    private readonly TestFolder _test = new();
    private readonly TestFileSystem _files = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly DataFolder _share;
    private readonly string _copy;

    public DataCopyKeptApartTests()
    {
        _share = DataFolder.Create(Path.Combine(_test.Root, "data"), _files);
        _copy = Directory.CreateDirectory(Path.Combine(_test.Root, "copy")).FullName;
    }

    public void Dispose() => _test.Dispose();

    /// <summary>
    /// A document damaged, edited in place, at a version out of order, or gone: its last good bytes at their version, also
    /// after a restart, while the share cannot be touched.
    /// </summary>
    [Theory]
    [InlineData(OutsideReason.Damaged)]
    [InlineData(OutsideReason.EditedInPlace)]
    [InlineData(OutsideReason.OutOfOrder)]
    [InlineData(OutsideReason.Gone)]
    public void KeptApart_OfADocumentChangedOutsideKiler_GivesItsLastGoodVersion_WithoutTheShare(OutsideReason reason)
    {
        Create(Settings, "first");
        Replace(Settings, 1, "second");
        Replace(Settings, 2, "third");
        var copy = OpenAt();
        copy.Load();
        var path = _share.DocumentPath(Settings);
        switch (reason)
        {
            case OutsideReason.Damaged:
                File.WriteAllText(path, "{", Encoding.UTF8);
                break;
            case OutsideReason.EditedInPlace:
                TestFolder.EditInPlace(path, DocumentFormat.Next(Settings, 2, Content("edited by hand")));
                break;
            case OutsideReason.OutOfOrder:
                TestFolder.WriteAsKiler(path, File.ReadAllBytes(_share.HistoryPath(Settings, 1)));
                break;
            case OutsideReason.Gone:
                File.Delete(path);
                break;
        }

        copy.Load();
        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();
        Assert.Equal(new OutsideChange(Settings, InTrash: false, reason, LastGood: 3), Assert.Single(copy.ToRestore()));
        _files.Before = (step, path) => throw new IOException("The share was touched.");

        foreach (var kept in (Document?[])[copy.KeptApart(Settings), OpenAt().KeptApart(Settings)])
        {
            Assert.NotNull(kept);
            Assert.Equal(Settings, kept.Key);
            Assert.Equal(3, kept.Version);
            Assert.Equal("third", (string?)kept.Content["name"]);
        }
    }

    /// <summary>A document whose file on the share is a version that Kiler made, and one that the copy never had, keep nothing apart.</summary>
    [Fact]
    public void KeptApart_OfADocumentNotChangedOutsideKiler_IsNull()
    {
        Create(Record, "first");
        Replace(Record, 1, "second");
        var copy = OpenAt();
        copy.Load();
        Assert.Equal(WriteOutcome.Written, copy.Replace(Record, 2, Content("third")).Outcome);

        Assert.Null(copy.KeptApart(Record));
        Assert.Null(copy.KeptApart(OtherRecord));
    }

    /// <summary>
    /// A change outside Kiler that the copy has forgotten (M2.9): one edited in place takes the bytes of the share as its last
    /// good ones, so nothing is kept apart any more.
    /// </summary>
    [Fact]
    public void KeptApart_OfAChangeThatTheCopyForgot_IsNull()
    {
        Create(Record, "first");
        var copy = OpenAt();
        copy.Load();
        TestFolder.EditInPlace(_share.DocumentPath(Record), DocumentFormat.New(Record, Content("edited by hand")));
        copy.Load();
        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();
        Assert.NotNull(copy.KeptApart(Record));

        Assert.Equal(1, copy.Forget(copy.ToRestore()));

        Assert.Null(copy.KeptApart(Record));
    }

    /// <summary>
    /// The kept file whose bytes are not those of its entry, as a crash between the file and the index, or a hand in the
    /// profile, can leave it, gives nothing; one that cannot be read now comes back once it can.
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("other bytes")]
    [InlineData("held")]
    [InlineData("another version")]
    [InlineData("damaged")]
    public void KeptApart_WhoseFileIsNotTheOneOfItsEntry_IsNull(string how)
    {
        Create(Record, "first");
        var copy = OpenAt();
        copy.Load();
        File.WriteAllText(_share.DocumentPath(Record), "{", Encoding.UTF8);
        copy.Load();
        var path = KeptPath(Record);
        FileStream? held = null;
        switch (how)
        {
            case "missing":
                File.Delete(path);
                break;
            case "other bytes":
                File.WriteAllBytes(path, DocumentFormat.New(Record, Content("other")));
                break;
            case "held":
                held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                break;
            case "another version":
                Swap(path, DocumentFormat.Next(Record, 1, Content("other")));
                copy = OpenAt();
                break;
            case "damaged":
                Swap(path, Encoding.UTF8.GetBytes("{ \"id\": \"8f3a1c\", \"version\": 1, "));
                copy = OpenAt();
                break;
        }

        try
        {
            Assert.Null(copy.KeptApart(Record));
        }
        finally
        {
            held?.Dispose();
        }

        if (held is not null)
        {
            Assert.Equal(1, copy.KeptApart(Record)?.Version);
        }
    }

    /// <summary>The last good bytes that the copy keeps for a document of the same id in the trash are not those of the document in its folder.</summary>
    [Fact]
    public void KeptApart_GivesNothingOfTheTrash()
    {
        Create(Record, "deleted");
        var copy = OpenAt();
        copy.Load();
        Assert.Equal(WriteOutcome.Deleted, _share.DeleteDocument(Record, 1).Outcome);
        copy.Load();
        File.WriteAllText(_share.TrashPath(Record), "{", Encoding.UTF8);
        copy.Load();
        Assert.Equal(new OutsideChange(Record, InTrash: true, OutsideReason.Damaged, LastGood: 1), Assert.Single(copy.ToRestore()));

        Assert.Null(copy.KeptApart(Record));
    }

    /// <summary>The copy gives no document of the private folder of another user (M2.7c), so a key of one is a fault of the caller.</summary>
    [Fact]
    public void KeptApart_OfAKeyOfAnotherPrivateFolder_IsRefused()
    {
        var copy = DataCopy.Open(_share, _copy, _clock, Me);

        Assert.Throws<ArgumentException>(() => copy.KeptApart(Record.InPrivate("demo.other")));
        Assert.Null(copy.KeptApart(Record.InPrivate(Me)));
        Assert.Throws<ArgumentException>(() => OpenAt().KeptApart(Record.InPrivate(Me)));
    }

    private static JsonObject Content(string name) => new() { ["name"] = name };

    private DataCopy OpenAt() => DataCopy.Open(_share, _copy, _clock);

    /// <summary>A document on the share, as a write of another Kiler makes it.</summary>
    private void Create(DocumentKey key, string name) => Assert.Equal(WriteOutcome.Written, _share.CreateDocument(key, Content(name)).Outcome);

    /// <summary>A new version of a document on the share, as a write of another Kiler makes it, with the old one in its history slot.</summary>
    private void Replace(DocumentKey key, int version, string name) => Assert.Equal(WriteOutcome.Written, _share.ReplaceDocument(key, version, Content(name)).Outcome);

    /// <summary>Puts <paramref name="bytes"/> into the kept file at <paramref name="path"/> with an index whose entry names them, as a hand in the profile could.</summary>
    private void Swap(string path, byte[] bytes)
    {
        File.WriteAllBytes(path, bytes);
        var indexPath = Path.Combine(_copy, "index.json");
        var index = JsonNode.Parse(File.ReadAllBytes(indexPath))!;
        var kept = index["files"]!.AsArray().Single(entry => (string?)entry!["id"] == Record.Id)!["kept"]!;
        kept["hash"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        kept["length"] = bytes.Length;
        File.WriteAllText(indexPath, index.ToJsonString());
    }

    /// <summary>The last good bytes kept apart in the copy: <c>kept\records\8f3a1c.json</c> (M2.7a).</summary>
    private string KeptPath(DocumentKey key) => Path.Combine(_copy, "kept", key.Folder ?? "", key.Id + ".json");
}

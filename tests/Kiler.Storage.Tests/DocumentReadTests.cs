using System.Text;
using System.Text.Json.Nodes;

namespace Kiler.Storage.Tests;

/// <summary>Reading document files from a data folder and a private folder (M2.2).</summary>
public sealed class DocumentReadTests : IDisposable
{
    private static readonly DocumentKey Record = DocumentKey.InFolder(DataFolder.Records, "8f3a1c");

    private readonly TestFolder _test = new();
    private readonly string _data;

    public DocumentReadTests() => _data = Path.Combine(_test.Root, "data");

    public void Dispose() => _test.Dispose();

    [Fact]
    public void Read_ADocumentFile_GivesTheDocument()
    {
        var data = DataFolder.Create(_data);
        var content = new JsonObject { ["template"] = "db", ["name"] = "Демо база" };
        File.WriteAllBytes(data.DocumentPath(Record), DocumentFormat.Next(Record, 6, content));

        var read = data.Read(Record);

        Assert.NotNull(read);
        Assert.False(read.IsDamaged, $"Damaged: {read.Damage}");
        Assert.Equal(Record, read.Document.Key);
        Assert.Equal(7, read.Document.Version);
        Assert.True(JsonNode.DeepEquals(content, read.Document.Content), read.Document.Content.ToJsonString());
    }

    [Fact]
    public void Read_ARootDocument_GivesTheDocument()
    {
        var data = DataFolder.Create(_data);
        var levels = DocumentKey.AtRoot(DataFolder.Levels);
        File.WriteAllBytes(data.DocumentPath(levels), DocumentFormat.New(levels, new JsonObject { ["count"] = 4 }));

        var read = data.Read(levels);

        Assert.NotNull(read);
        Assert.Equal(1, read.Document?.Version);
    }

    [Fact]
    public void Read_ADocumentInAPrivateFolder_GivesTheDocument()
    {
        var mine = DataFolder.Create(_data).CreatePrivate("demo.user");
        var key = DocumentKey.AtRoot(DataFolder.Key);
        File.WriteAllBytes(mine.DocumentPath(key), DocumentFormat.New(key, new JsonObject { ["wrapped"] = "a2V5" }));
        File.WriteAllBytes(mine.DocumentPath(Record), DocumentFormat.New(Record, new JsonObject { ["name"] = "лично" }));

        Assert.Equal(1, mine.Read(key)?.Document?.Version);
        Assert.Equal("лично", (string?)mine.Read(Record)?.Document?.Content["name"]);
    }

    [Fact]
    public void Read_AMissingDocument_GivesNothing()
    {
        var data = DataFolder.Create(_data);

        Assert.Null(data.Read(Record));
    }

    [Fact]
    public void Read_ADamagedFile_GivesTheReasonAndThrowsNothing()
    {
        var data = DataFolder.Create(_data);
        File.WriteAllText(data.DocumentPath(Record), "{\"id\": \"8f3a1c\", \"version\": ", Encoding.UTF8);

        Assert.Equal(DocumentDamage.NotJson, data.Read(Record)?.Damage);
    }

    /// <summary>What a crash in the middle of a write can leave: an empty file, or one of zeros.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4096)]
    public void Read_AnEmptyOrZeroedFile_IsNotJson(int size)
    {
        var data = DataFolder.Create(_data);
        File.WriteAllBytes(data.DocumentPath(Record), new byte[size]);

        Assert.Equal(DocumentDamage.NotJson, data.Read(Record)?.Damage);
    }

    [Fact]
    public void Read_AFolderInPlaceOfTheFile_Throws()
    {
        var data = DataFolder.Create(_data);
        Directory.CreateDirectory(data.DocumentPath(Record));

        Assert.Throws<UnauthorizedAccessException>(() => data.Read(Record));
    }

    [Fact]
    public void Read_AFileWithTheIdOfAnotherFile_IsWrongId()
    {
        var data = DataFolder.Create(_data);
        var other = DocumentKey.InFolder(DataFolder.Records, "b72e09");
        File.WriteAllBytes(data.DocumentPath(other), DocumentFormat.New(Record, new JsonObject()));

        Assert.Equal(DocumentDamage.WrongId, data.Read(other)?.Damage);
    }

    [Fact]
    public void Read_AFileOverTheSizeLimit_IsNotReadWhole()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        using (var file = File.Create(data.DocumentPath(Record)))
        {
            file.Write("""{"id": "8f3a1c", "version": 1}"""u8);
            file.SetLength(DocumentFormat.MaxSize + 1);
        }

        Assert.Equal(DocumentDamage.TooLarge, data.Read(Record)?.Damage);
        Assert.Equal(0, files.BytesRead);
    }
}

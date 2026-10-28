using System.Text;
using System.Text.Json.Nodes;

namespace Kiler.Storage.Tests;

public class DocumentFormatTests
{
    private const char Replacement = (char)0xFFFD;

    private static readonly DocumentKey Key = DocumentKey.InFolder(DataFolder.Records, "8f3a1c");

    /// <summary>The damaged files, each with its reason. Every one is a document of <see cref="Key"/> but for its fault.</summary>
    public static TheoryData<string, DocumentDamage> DamagedFiles => new()
    {
        { "", DocumentDamage.NotJson },
        { "   ", DocumentDamage.NotJson },
        { """{"id": "8f3a1c", "version": 1""", DocumentDamage.NotJson },
        { """{"id": "8f3a1c", "version": 1,}""", DocumentDamage.NotJson },
        { """{"id": "8f3a1c", /* made up */ "version": 1}""", DocumentDamage.NotJson },
        { """{'id': '8f3a1c', 'version': 1}""", DocumentDamage.NotJson },
        { """{"id": "8f3a1c", "version": 1} {}""", DocumentDamage.NotJson },
        { """{"id": "8f3a1c", "version": 1, "id": "b72e09"}""", DocumentDamage.NotJson },
        { $$"""{"id": "8f3a1c", "version": 1, "{{Escaped("id")}}": "b72e09"}""", DocumentDamage.NotJson },
        { """{"id": "8f3a1c", "version": 1, "version": 2}""", DocumentDamage.NotJson },
        { """{"id": "8f3a1c", "version": 1, "public": {"name": "a", "name": "b"}}""", DocumentDamage.NotJson },
        { """{"id": "8f3a1c", "version": 1, "name": "\uD800"}""", DocumentDamage.NotJson },
        { """{"id": "8f3a1c", "version": 1, "\uDC00": 1}""", DocumentDamage.NotJson },
        { """{"id": "8f3a1c", "version": 1, "a": """ + Nested(MaxDepthLeft + 1) + "}", DocumentDamage.NotJson },
        { "null", DocumentDamage.NotAnObject },
        { "[]", DocumentDamage.NotAnObject },
        { """[{"id": "8f3a1c", "version": 1}]""", DocumentDamage.NotAnObject },
        { "\"8f3a1c\"", DocumentDamage.NotAnObject },
        { "1", DocumentDamage.NotAnObject },
        { """{"version": 1}""", DocumentDamage.WrongId },
        { """{"id": null, "version": 1}""", DocumentDamage.WrongId },
        { """{"id": 8, "version": 1}""", DocumentDamage.WrongId },
        { """{"id": ["8f3a1c"], "version": 1}""", DocumentDamage.WrongId },
        { """{"id": "b72e09", "version": 1}""", DocumentDamage.WrongId },
        { """{"id": "8F3A1C", "version": 1}""", DocumentDamage.WrongId },
        { """{"id": "8f3a1c ", "version": 1}""", DocumentDamage.WrongId },
        { """{"id": "", "version": 1}""", DocumentDamage.WrongId },
        { """{"Id": "8f3a1c", "version": 1}""", DocumentDamage.WrongId },
        { """{"id": "8f3a1c", "version": 1, "ID": "b72e09"}""", DocumentDamage.WrongId },
        { """{"iD": "8f3a1c", "id": "8f3a1c", "version": 1}""", DocumentDamage.WrongId },
        { """{"id": "8f3a1c"}""", DocumentDamage.WrongVersion },
        { """{"id": "8f3a1c", "version": null}""", DocumentDamage.WrongVersion },
        { """{"id": "8f3a1c", "version": 0}""", DocumentDamage.WrongVersion },
        { """{"id": "8f3a1c", "version": -1}""", DocumentDamage.WrongVersion },
        { """{"id": "8f3a1c", "version": 1.5}""", DocumentDamage.WrongVersion },
        { """{"id": "8f3a1c", "version": 1.0}""", DocumentDamage.WrongVersion },
        { """{"id": "8f3a1c", "version": 1e1}""", DocumentDamage.WrongVersion },
        { """{"id": "8f3a1c", "version": "1"}""", DocumentDamage.WrongVersion },
        { """{"id": "8f3a1c", "version": true}""", DocumentDamage.WrongVersion },
        { """{"id": "8f3a1c", "version": 2147483648}""", DocumentDamage.WrongVersion },
        { """{"id": "8f3a1c", "Version": 1}""", DocumentDamage.WrongVersion },
        { """{"id": "8f3a1c", "version": 1, "VERSION": 99}""", DocumentDamage.WrongVersion },
    };

    /// <summary>The levels of nesting that a field can have: the document object is the first.</summary>
    private static int MaxDepthLeft => DocumentFormat.MaxDepth - 1;

    [Fact]
    public void New_WritesIdAndVersionFirstThenTheContent_IndentedWithLfInUtf8()
    {
        var content = Content("""{"template": "db", "public": {"name": "Демо база", "tags": ["плащания"]}}""");

        var bytes = DocumentFormat.New(Key, content);

        var expected = """
            {
              "id": "8f3a1c",
              "version": 1,
              "template": "db",
              "public": {
                "name": "Демо база",
                "tags": [
                  "плащания"
                ]
              }
            }

            """.ReplaceLineEndings("\n");
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), "A byte order mark was written.");
    }

    [Fact]
    public void New_IsVersionOneWhateverTheContentSays_AndLeavesTheContentAsItWas()
    {
        var json = """{"version": 7, "id": "b72e09", "name": "demo", "ID": "b72e09", "Version": 8}""";
        var content = Content(json);

        var read = Read(DocumentFormat.New(Key, content));

        Assert.Equal(1, read.Version);
        Assert.Equal("8f3a1c", read.Key.Id);
        Assert.True(JsonNode.DeepEquals(Content("""{"name": "demo"}"""), read.Content), read.Content.ToJsonString());
        Assert.True(JsonNode.DeepEquals(Content(json), content), content.ToJsonString());
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(6, 7)]
    [InlineData(int.MaxValue - 1, int.MaxValue)]
    public void Next_IsOneMoreThanTheVersionItChangesWhateverTheContentSays(int changes, int expected)
    {
        var content = Content("""{"name": "demo", "version": 2}""");

        var read = Read(DocumentFormat.Next(Key, changes, content));

        Assert.Equal(expected, read.Version);
        Assert.True(JsonNode.DeepEquals(Content("""{"name": "demo"}"""), read.Content), read.Content.ToJsonString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Next_OfAVersionBelowOne_IsRefused(int changes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentFormat.Next(Key, changes, new JsonObject()));

    [Fact]
    public void Next_OfTheLastVersion_IsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentFormat.Next(Key, int.MaxValue, new JsonObject()));

    [Fact]
    public void RoundTrip_KeepsCyrillicAndFieldsTheLayerDoesNotKnow()
    {
        var json = """
            {
              "template": "db",
              "createdAt": "2026-09-30T08:10:00Z",
              "public": {
                "stash": ["a1", "b2"],
                "name": "Демо база – основна",
                "description": "Кавички \" и наклонена \\ черта, <b>&amp;</b>, ред\nнов, таб\t, 😀",
                "env": "prod",
                "empty": {},
                "none": []
              },
              "protected": {"1": "kJ8sd0f9Lp==", "3": "Zx81Lq0vNm=="},
              "поле \"с\" <име>": "стойност",
              "port": 1521,
              "ratio": 1.50,
              "large": 1e400,
              "on": true,
              "off": false,
              "missing": null
            }
            """;
        var content = Content(json);

        var first = DocumentFormat.New(Key, content);
        var read = Read(first);

        Assert.Equal(Key, read.Key);
        Assert.Equal(1, read.Version);
        Assert.True(JsonNode.DeepEquals(Content(json), read.Content), read.Content.ToJsonString());
        Assert.Equal(Names(Content(json)), Names(read.Content));

        // Written again, only the version changes: the numbers keep their text and the strings their characters.
        var second = DocumentFormat.Next(Key, read.Version, read.Content);
        Assert.Equal(Encoding.UTF8.GetString(first).Replace("\"version\": 1,", "\"version\": 2,", StringComparison.Ordinal), Encoding.UTF8.GetString(second));
        Assert.Contains("\"ratio\": 1.50,", Encoding.UTF8.GetString(second), StringComparison.Ordinal);
        Assert.Contains("Демо база – основна", Encoding.UTF8.GetString(second), StringComparison.Ordinal);
    }

    [Fact]
    public void RoundTrip_ARootDocument_HasItsNameAsId()
    {
        var key = DocumentKey.AtRoot(DataFolder.Settings);

        var bytes = DocumentFormat.New(key, Content("""{"guests": false}"""));
        var read = Read(bytes, key);

        Assert.StartsWith("{\n  \"id\": \"settings\",\n  \"version\": 1,\n", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(key, read.Key);
    }

    [Theory]
    [MemberData(nameof(DamagedFiles))]
    public void Read_ADamagedFile_GivesTheReason(string json, DocumentDamage damage)
    {
        var read = DocumentFormat.Read(Key, Encoding.UTF8.GetBytes(json));

        Assert.True(read.IsDamaged, "The file was read as a document.");
        Assert.Equal(damage, read.Damage);
        Assert.Null(read.Document);
    }

    [Fact]
    public void Read_InvalidUtf8_IsNotJson()
    {
        byte[] bytes = [.. """{"id": "8f3a1c", "version": 1, "name": """u8, (byte)'"', 0xC3, 0x28, (byte)'"', (byte)'}'];

        Assert.Equal(DocumentDamage.NotJson, DocumentFormat.Read(Key, bytes).Damage);
    }

    [Theory]
    [MemberData(nameof(DamagedFiles))]
    public void ReadHead_ADamagedFile_GivesTheReasonOfTheRead(string json, DocumentDamage damage) =>
        Assert.Equal(new DocumentHead(0, damage, null), DocumentFormat.ReadHead(Key, Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void ReadHead_InvalidUtf8AndAFileTooLarge_AreDamagedAsInTheRead()
    {
        byte[] invalid = [.. """{"id": "8f3a1c", "version": 1, "name": """u8, (byte)'"', 0xC3, 0x28, (byte)'"', (byte)'}'];

        Assert.Equal(DocumentDamage.NotJson, DocumentFormat.ReadHead(Key, invalid).Damage);
        Assert.Equal(DocumentDamage.TooLarge, DocumentFormat.ReadHead(Key, Padded("""{"id": "8f3a1c", "version": 1}"""u8, DocumentFormat.MaxSize + 1)).Damage);
    }

    [Fact]
    public void ReadHead_ADocument_GivesItsVersion_WhereverIdAndVersionAre_AndAfterAByteOrderMark()
    {
        var bytes = DocumentFormat.Next(Key, 6, Content("""{"name": "demo"}"""));
        byte[] withBom = [.. Encoding.UTF8.Preamble, .. bytes];

        Assert.Equal(new DocumentHead(7, null, null), DocumentFormat.ReadHead(Key, bytes));
        Assert.Equal(new DocumentHead(7, null, null), DocumentFormat.ReadHead(Key, withBom));
        Assert.Equal(new DocumentHead(3, null, null), DocumentFormat.ReadHead(Key, """{"name": "a", "version": 3, "id": "8f3a1c"}"""u8.ToArray()));
    }

    [Fact]
    public void ReadHead_MakesNoNodesOfTheContent_AsTheReadDoes()
    {
        var items = new JsonArray([.. Enumerable.Range(0, 20_000).Select(i => (JsonNode)new JsonObject { ["a"] = i })]);
        var bytes = DocumentFormat.New(Key, new JsonObject { ["items"] = items });

        // Once first, for the code and the pooled buffers of the parser.
        DocumentFormat.ReadHead(Key, bytes);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var head = DocumentFormat.ReadHead(Key, bytes);
        var headBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        DocumentFormat.Read(Key, bytes);
        before = GC.GetAllocatedBytesForCurrentThread();
        DocumentFormat.Read(Key, bytes);
        var readBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(new DocumentHead(1, null, null), head);
        Assert.True(headBytes < 64 * 1024, $"The head of a document of {bytes.Length} bytes took {headBytes} bytes.");
        Assert.True(readBytes > bytes.Length, $"The whole read of a document of {bytes.Length} bytes took only {readBytes} bytes.");
    }

    [Fact]
    public void Read_IdAndVersionAnywhereInTheObject_AreFound()
    {
        var read = Read("""{"template": "db", "version": 3, "name": "demo", "id": "8f3a1c"}"""u8.ToArray());

        Assert.Equal(3, read.Version);
        Assert.True(JsonNode.DeepEquals(Content("""{"template": "db", "name": "demo"}"""), read.Content), read.Content.ToJsonString());
    }

    [Fact]
    public void Read_EscapedIdAndVersion_AreTheSameAsPlainOnes()
    {
        var read = Read(Encoding.UTF8.GetBytes($$"""{"{{Escaped("id")}}": "{{Escaped("8f3a1c")}}", "{{Escaped("version")}}": 2}"""));

        Assert.Equal(2, read.Version);
        Assert.Empty(read.Content);
    }

    [Fact]
    public void Read_TheLastVersion_IsRead() =>
        Assert.Equal(int.MaxValue, Read("""{"id": "8f3a1c", "version": 2147483647}"""u8.ToArray()).Version);

    [Fact]
    public void Read_AByteOrderMark_IsSkipped()
    {
        byte[] bytes = [.. Encoding.UTF8.Preamble, .. """{"id": "8f3a1c", "version": 4}"""u8];

        Assert.Equal(4, Read(bytes).Version);
    }

    [Fact]
    public void Read_NestedToTheLimit_IsRead()
    {
        var read = Read(Encoding.UTF8.GetBytes("""{"id": "8f3a1c", "version": 1, "a": """ + Nested(MaxDepthLeft) + "}"));

        Assert.Equal(1, read.Version);
    }

    [Fact]
    public void Read_AtTheSizeLimit_IsRead()
    {
        var bytes = Padded("""{"id": "8f3a1c", "version": 1}"""u8, DocumentFormat.MaxSize);

        Assert.Equal(1, Read(bytes).Version);
    }

    [Fact]
    public void Read_OverTheSizeLimit_IsTooLarge()
    {
        var bytes = Padded("""{"id": "8f3a1c", "version": 1}"""u8, DocumentFormat.MaxSize + 1);

        Assert.Equal(DocumentDamage.TooLarge, DocumentFormat.Read(Key, bytes).Damage);
    }

    [Fact]
    public void ReadStream_OverTheSizeLimit_ReadsNothing()
    {
        var bytesRead = 0L;
        using var stream = new CountingStream(new MemoryStream(Padded("{}"u8, DocumentFormat.MaxSize + 1)), read => bytesRead += read);

        Assert.Equal(DocumentDamage.TooLarge, DocumentFormat.Read(Key, stream).Damage);
        Assert.Equal(0, bytesRead);
    }

    [Fact]
    public void ReadStream_ThatGrowsWhileItIsRead_StopsAfterTheLimit()
    {
        var bytesRead = 0L;
        var bytes = Padded("""{"id": "8f3a1c", "version": 1}"""u8, (3 * DocumentFormat.MaxSize) + 5);
        using var stream = new CountingStream(new MemoryStream(bytes), read => bytesRead += read, length: 100);

        Assert.Equal(DocumentDamage.TooLarge, DocumentFormat.Read(Key, stream).Damage);
        Assert.InRange(bytesRead, 0L, DocumentFormat.MaxSize + 1L);
    }

    /// <summary>A file of <paramref name="size"/> bytes whose handle gives <paramref name="length"/>, as when it grows or shrinks.</summary>
    [Theory]
    [InlineData(DocumentFormat.MaxSize, DocumentFormat.MaxSize, false)]
    [InlineData(DocumentFormat.MaxSize, DocumentFormat.MaxSize - 1, false)]
    [InlineData(DocumentFormat.MaxSize, 0, false)]
    [InlineData(DocumentFormat.MaxSize + 1, DocumentFormat.MaxSize, true)]
    [InlineData(DocumentFormat.MaxSize + 1, 0, true)]
    public void ReadStream_AtTheSizeLimit_ReadsUpToItAndNotPast(int size, long length, bool tooLarge)
    {
        var bytesRead = 0L;
        var bytes = Padded("""{"id": "8f3a1c", "version": 1}"""u8, size);
        using var stream = new CountingStream(new MemoryStream(bytes), read => bytesRead += read, length);

        var read = DocumentFormat.Read(Key, stream);

        Assert.Equal(tooLarge ? DocumentDamage.TooLarge : (DocumentDamage?)null, read.Damage);
        Assert.InRange(bytesRead, 0L, DocumentFormat.MaxSize + 1L);
    }

    [Fact]
    public void ReadStream_OneByteAtATime_ReadsItWhole()
    {
        var bytes = Padded("""{"id": "8f3a1c", "version": 3}"""u8, 500);
        using var stream = new CountingStream(new MemoryStream(bytes), _ => { }, maxChunk: 1);

        Assert.Equal(3, DocumentFormat.Read(Key, stream).Document?.Version);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(1000)]
    public void ReadStream_ThatChangesSizeWhileItIsRead_ReadsItWhole(long length)
    {
        var bytes = Padded("""{"id": "8f3a1c", "version": 5}"""u8, 500);
        using var stream = new CountingStream(new MemoryStream(bytes), _ => { }, length);

        var read = DocumentFormat.Read(Key, stream);

        Assert.False(read.IsDamaged, $"Damaged: {read.Damage}");
        Assert.Equal(5, read.Document.Version);
    }

    [Fact]
    public void New_OverTheSizeLimit_IsRefused()
    {
        var content = new JsonObject { ["text"] = new string('a', DocumentFormat.MaxSize) };

        Assert.Throws<ArgumentException>(() => DocumentFormat.New(Key, content));
    }

    [Fact]
    public void New_NestedToTheLimit_IsReadBack()
    {
        var content = new JsonObject { ["a"] = JsonNode.Parse(Nested(MaxDepthLeft)) };

        var read = Read(DocumentFormat.New(Key, content));

        Assert.True(JsonNode.DeepEquals(content, read.Content), "The nesting changed.");
    }

    [Fact]
    public void New_NestedDeeperThanTheLimit_IsRefused()
    {
        var content = new JsonObject { ["a"] = JsonNode.Parse(Nested(MaxDepthLeft + 1)) };

        Assert.Throws<ArgumentException>(() => DocumentFormat.New(Key, content));
    }

    [Fact]
    public void New_ALoneSurrogateInTheContent_IsWrittenAsTheReplacementCharacterAndReadsBack()
    {
        var content = new JsonObject { ["name"] = "a" + (char)0xD800 + "b", [((char)0xDC00).ToString()] = 1 };

        var read = Read(DocumentFormat.New(Key, content));

        Assert.Equal($"a{Replacement}b", (string?)read.Content["name"]);
        Assert.Equal(1, (int?)read.Content[Replacement.ToString()]);
    }

    private static JsonObject Content(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>
    /// Every character as a JSON escape: a backslash, <c>u</c> and four hex digits. Made in code, because the tools that
    /// write these files turn such an escape in their text into the character.
    /// </summary>
    private static string Escaped(string text) => string.Concat(text.Select(character => $"\\u{(int)character:x4}"));

    private static Document Read(byte[] bytes, DocumentKey? key = null)
    {
        var read = DocumentFormat.Read(key ?? Key, bytes);
        Assert.False(read.IsDamaged, $"Damaged: {read.Damage}");
        return read.Document;
    }

    private static string[] Names(JsonObject content) => [.. content.Select(property => property.Key)];

    /// <summary>Objects nested <paramref name="levels"/> deep: <c>{"a": {"a": {}}}</c> has 3.</summary>
    private static string Nested(int levels) => string.Concat(Enumerable.Repeat("{\"a\": ", levels - 1)) + "{}" + new string('}', levels - 1);

    /// <summary>The JSON with spaces after it up to <paramref name="size"/> bytes, which is still the same document.</summary>
    private static byte[] Padded(ReadOnlySpan<byte> json, int size)
    {
        var bytes = new byte[size];
        bytes.AsSpan().Fill((byte)' ');
        json.CopyTo(bytes);
        return bytes;
    }
}

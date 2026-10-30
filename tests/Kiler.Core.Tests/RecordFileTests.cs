using System.Text.Json.Nodes;
using Kiler.Storage;

namespace Kiler.Core.Tests;

/// <summary>The models of a record and a stash (M5.3, design 5.2 and 6.6): each reads what it writes, and the contents that do not fit.</summary>
public class RecordFileTests
{
    private const string Dates = """ "createdBy":"demo.admin","createdAt":"2026-09-30T08:10:00Z","updatedBy":"demo.editor","updatedAt":"2026-10-21T11:32:00Z" """;

    private static readonly DocumentKey RecordKey = RecordFile.KeyOf(TestRecords.Id);

    private static readonly DocumentKey StashKey = StashFile.KeyOf(TestRecords.StashId);

    /// <summary>Records that do not fit, with the reason; <see cref="Record"/> puts a part in a record that fits otherwise.</summary>
    public static TheoryData<string, string> RecordsThatDoNotFit => new()
    {
        { Record(template: ""), "template is missing." },
        { Record(template: "\"template\":\"Db/x\""), "template is not the id of a template." },
        { Record(template: "\"template\":7"), ModelJson.WrongType },
        { Record(dates: """ "createdAt":"2026-09-30T08:10:00Z","updatedBy":"demo.editor","updatedAt":"2026-10-21T11:32:00Z" """), "createdBy is missing or is not the name of an account." },
        { Record(dates: """ "createdBy":"Demo Admin","createdAt":"2026-09-30T08:10:00Z","updatedBy":"demo.editor","updatedAt":"2026-10-21T11:32:00Z" """), "createdBy is missing or is not the name of an account." },
        { Record(dates: """ "createdBy":"demo.admin","createdAt":"2026-09-30T08:10:00Z","updatedAt":"2026-10-21T11:32:00Z" """), "updatedBy is missing or is not the name of an account." },
        { Record(dates: """ "createdBy":"demo.admin","createdAt":"2026-09-30","updatedBy":"demo.editor","updatedAt":"2026-10-21T11:32:00Z" """), "createdAt is missing or is not a date in UTC." },
        { Record(dates: """ "createdBy":"demo.admin","createdAt":"2026-09-30T08:10:00+02:00","updatedBy":"demo.editor","updatedAt":"2026-10-21T11:32:00Z" """), "createdAt is missing or is not a date in UTC." },
        { Record(dates: """ "createdBy":"demo.admin","createdAt":"2026-09-30T08:10:00Z","updatedBy":"demo.editor" """), "updatedAt is missing or is not a date in UTC." },
        { Record(dates: """ "createdBy":"demo.admin","createdAt":"2026-09-30T08:10:00Z","updatedBy":"demo.editor","updatedAt":20261021 """), ModelJson.WrongType },
        { "{\"template\":\"db\"," + Dates + ",\"protected\":{},\"fields\":{}}", "public is missing." },
        { Record(stash: ""), "public.stash is missing or has null." },
        { Record(stash: "\"stash\":[]"), "public.stash is not 1 to 4 ids of stashes." },
        { Record(stash: "\"stash\":[\"a1b2c3d4\",null]"), "public.stash is missing or has null." },
        { Record(stash: "\"stash\":[\"a1b2c3d4\",\"\"]"), "public.stash is not 1 to 4 ids of stashes." },
        { Record(stash: "\"stash\":[\"a1b2c3d4\",\"e5f6g7h8\",\"j9k0m1n2\",\"p3q4r5s6\",\"t7v8w9x0\"]"), "public.stash is not 1 to 4 ids of stashes." },
        { Record(stash: "\"stash\":[\"A1B2C3D4\"]"), "public.stash is not 1 to 4 ids of stashes." },
        { Record(stash: "\"stash\":[\"a1b2c3d\"]"), "public.stash is not 1 to 4 ids of stashes." },
        { Record(stash: "\"stash\":[\"a1b2c3di\"]"), "public.stash is not 1 to 4 ids of stashes." },
        { Record(stash: "\"stash\":[\"a1b2c3d4\",\"e5f6g7h8\",\"a1b2c3d4\"]"), "public.stash has a stash twice." },
        { Record(name: ""), "public.name is missing." },
        { Record(name: "\"name\":\" \""), "public.name is blank." },
        { Record(tags: ""), "public.tags is missing or has null." },
        { Record(tags: "\"tags\":[\"payments\",null]"), "public.tags is missing or has null." },
        { Record(tags: "\"tags\":[\"payments\",\" \"]"), "An entry of public.tags is blank." },
        { Record(tags: "\"tags\":\"payments\""), ModelJson.WrongType },
        { Record(publicFields: "\"env\":true"), "A field of public is not a text, a number or a code." },
        { Record(publicFields: "\"env\":[\"prod\"]"), "A field of public is not a text, a number or a code." },
        { Record(publicFields: "\"script\":{\"language\":\"bash\"}"), "A field of public is not a text, a number or a code." },
        { Record(publicFields: "\"script\":{\"language\":\"bash\",\"code\":\"ls\",\"more\":\"x\"}"), "A field of public is not a text, a number or a code." },
        { Record(publicFields: "\"script\":{\"language\":\"bash\",\"code\":7}"), "A field of public is not a text, a number or a code." },
        { Record(publicFields: "\"script\":{\"language\":\"bash\",\"language\":\"sql\"}"), "A field of public is not a text, a number or a code." },
        { Record(groups: "\"fields\":{}"), "protected is missing." },
        { Record(groups: "\"protected\":{}"), "fields is missing." },
        { Record(groups: "\"protected\":{\"0\":\"x\"},\"fields\":{\"0\":[\"host\"]}"), "protected has a name that is not the digit of a level from 1 to 9, or a group that is null." },
        { Record(groups: "\"protected\":{\"01\":\"x\"},\"fields\":{\"01\":[\"host\"]}"), "protected has a name that is not the digit of a level" },
        { Record(groups: "\"protected\":{\"10\":\"x\"},\"fields\":{\"10\":[\"host\"]}"), "protected has a name that is not the digit of a level" },
        { Record(groups: "\"protected\":{\"1\":null},\"fields\":{\"1\":[\"host\"]}"), "protected has a name that is not the digit of a level from 1 to 9, or a group that is null." },
        { Record(groups: "\"protected\":{\"1\":\"x\"},\"fields\":{\"1\":null}"), "fields has a name that is not the digit of a level from 1 to 9, or a list that is null or has null." },
        { Record(groups: "\"protected\":{\"1\":\"x\"},\"fields\":{\"1\":[null]}"), "fields has a name that is not the digit of a level from 1 to 9, or a list that is null or has null." },
        { Record(groups: "\"protected\":{\"1\":\"x\"},\"fields\":{\"+1\":[\"host\"]}"), "fields has a name that is not the digit of a level" },
        { Record(groups: "\"protected\":{\"1\":\"x\"},\"fields\":{}"), "fields does not name the levels of protected." },
        { Record(groups: "\"protected\":{\"1\":\"x\"},\"fields\":{\"1\":[\"host\"],\"3\":[\"user\"]}"), "fields does not name the levels of protected." },
        { Record(groups: "\"protected\":{\"1\":\"x\",\"3\":\"y\"},\"fields\":{\"1\":[\"host\"],\"3\":[\"host\"]}"), "fields names a field twice, or a field of public." },
        { Record(groups: "\"protected\":{\"1\":\"x\"},\"fields\":{\"1\":[\"host\",\"host\"]}"), "fields names a field twice, or a field of public." },
        { Record(publicFields: "\"env\":\"prod\"", groups: "\"protected\":{\"1\":\"x\"},\"fields\":{\"1\":[\"env\"]}"), "fields names a field twice, or a field of public." },
        { Record(groups: "\"protected\":[\"x\"],\"fields\":{}"), ModelJson.WrongType },
    };

    /// <summary>Stashes that do not fit, with the reason.</summary>
    public static TheoryData<string, string> StashesThatDoNotFit => new()
    {
        { $$"""{{{Dates}}}""", "name is missing." },
        { $$"""{"name":" ",{{Dates}}}""", "name is blank." },
        { $$"""{"name":7,{{Dates}}}""", ModelJson.WrongType },
        { """{"name":"Payments","createdBy":"demo.admin","createdAt":"2026-09-30T08:10:00Z","updatedBy":"demo.editor"}""", "updatedAt is missing or is not a date in UTC." },
        { """{"name":"Payments","createdBy":"demo.admin","createdAt":"30.09.2026","updatedBy":"demo.editor","updatedAt":"2026-10-21T11:32:00Z"}""", "createdAt is missing or is not a date in UTC." },
    };

    [Fact]
    public void RecordFile_ReadsWhatItWrites_ThroughTheDataLayer()
    {
        using var data = new TestData();
        var record = new RecordFile(
            TestRecords.Id,
            "db",
            new Authorship("demo.admin", new DateTime(2026, 9, 30, 8, 10, 0, DateTimeKind.Utc), "demo.editor", new DateTime(2026, 10, 21, 11, 32, 0, 500, DateTimeKind.Utc)),
            ["a1b2c3d4", "e5f6g7h8", "j9k0m1n2", "p3q4r5s6"],
            "Payments database",
            ["payments", "Плащания"],
            new Dictionary<string, FieldValue>
            {
                ["env"] = FieldValue.OfText("prod"),
                ["description"] = FieldValue.OfText("The main database"),
                ["port"] = FieldValue.OfNumber(1521),
                ["script"] = FieldValue.OfCode("bash", "ls -l"),
            },
            new Dictionary<int, string> { [3] = "made-up group 3", [1] = "made-up group 1" },
            new Dictionary<int, IReadOnlyList<string>> { [1] = ["host"], [3] = ["user", "password"] });

        var document = data.WriteAndRead(record.Key, record.ToContent());

        Assert.True(RecordFile.TryRead(document, out var read, out var invalid), invalid);
        Assert.Equal((TestRecords.Id, "db", "Payments database"), (read.Id, read.Template, read.Name));
        Assert.Equal(("demo.admin", "demo.editor"), (read.Authorship.CreatedBy, read.Authorship.UpdatedBy));
        Assert.Equal((record.Authorship.CreatedAt, record.Authorship.UpdatedAt, DateTimeKind.Utc), (read.Authorship.CreatedAt, read.Authorship.UpdatedAt, read.Authorship.UpdatedAt.Kind));
        Assert.Equal(record.Stash, read.Stash);
        Assert.Equal(record.Tags, read.Tags);
        Assert.Equal((FieldValueKind.Text, "prod"), (read.Public["env"].Kind, read.Public["env"].Text));
        Assert.Equal((FieldValueKind.Number, 1521d), (read.Public["port"].Kind, read.Public["port"].Number));
        Assert.Equal((FieldValueKind.Code, "bash", "ls -l"), (read.Public["script"].Kind, read.Public["script"].Language, read.Public["script"].Text));
        Assert.Equal([1, 3], read.Protected.Keys);
        Assert.Equal(["user", "password"], read.Fields[3]);
        Assert.Equal(record.ToContent().ToJsonString(), read.ToContent().ToJsonString());
        Assert.Equal("2026-10-21T11:32:00.5Z", (string?)read.ToContent()["updatedAt"]);
        Assert.Equal("2026-09-30T08:10:00Z", (string?)read.ToContent()["createdAt"]);
    }

    [Fact]
    public void ARecord_WithoutGroups_AndANullOrEmptyField_Fits_WithoutThatField()
    {
        Assert.True(RecordFile.TryRead(RecordKey, Content(Record(publicFields: "\"env\":null,\"host\":\"\",\"port\":0")), out var record, out var invalid), invalid);

        Assert.Equal(["port"], record.Public.Keys);
        Assert.Empty(record.Protected);
    }

    [Theory]
    [MemberData(nameof(RecordsThatDoNotFit))]
    public void ARecordThatDoesNotFit_IsInvalid_WithAReason(string json, string reason)
    {
        Assert.False(RecordFile.TryRead(RecordKey, Content(json), out var record, out var invalid));
        Assert.Null(record);
        Assert.Contains(reason, invalid, StringComparison.Ordinal);
    }

    [Fact]
    public void ARecordFile_WhoseNameIsNotAnId_IsInvalid()
    {
        Assert.False(RecordFile.TryRead(RecordFile.KeyOf("8f3a1c"), Content(Record()), out _, out var invalid));
        Assert.Equal("The name of the file is not the id of a record.", invalid);
        Assert.False(StashFile.TryRead(StashFile.KeyOf("stash-01"), Content($$"""{"name":"Payments",{{Dates}}}"""), out _, out invalid));
        Assert.Equal("The name of the file is not the id of a stash.", invalid);
    }

    [Fact]
    public void AReason_NeverRepeatsAValueOfTheShare()
    {
        Assert.False(RecordFile.TryRead(RecordKey, Content(Record(publicFields: "\"secretValue1\":[\"made-up-value\"]")), out _, out var invalid));
        Assert.DoesNotContain("secretValue1", invalid, StringComparison.Ordinal);
        Assert.DoesNotContain("made-up-value", invalid, StringComparison.Ordinal);
        Assert.False(RecordFile.TryRead(RecordKey, Content(Record(groups: "\"protected\":{\"1\":\"x\"},\"fields\":{\"1\":[\"secretValue1\",\"secretValue1\"]}")), out _, out invalid));
        Assert.DoesNotContain("secretValue1", invalid, StringComparison.Ordinal);
    }

    [Fact]
    public void TheConstructors_RefuseWhatDoesNotFit()
    {
        var authors = TestRecords.Authors;
        var none = new Dictionary<string, FieldValue>();
        var noGroups = new Dictionary<int, string>();
        var noLists = new Dictionary<int, IReadOnlyList<string>>();
        Assert.Throws<ArgumentException>(() => new RecordFile("K3X9Q2MF", "db", authors, [TestRecords.StashId], "Name", [], none, noGroups, noLists));
        Assert.Throws<ArgumentException>(() => new RecordFile(TestRecords.Id, "db", authors, [], "Name", [], none, noGroups, noLists));
        Assert.Throws<ArgumentException>(() => new RecordFile(TestRecords.Id, "db", authors, [TestRecords.StashId], "Name", [], new Dictionary<string, FieldValue> { ["name"] = FieldValue.OfText("x") }, noGroups, noLists));
        Assert.Throws<ArgumentException>(() => new RecordFile(TestRecords.Id, "db", authors, [TestRecords.StashId], "Name", [], none, new Dictionary<int, string> { [0] = "x" }, new Dictionary<int, IReadOnlyList<string>> { [0] = [] }));
        Assert.Throws<ArgumentException>(() => new StashFile(TestRecords.StashId, "", authors));
        Assert.Throws<ArgumentException>(() => new Authorship("demo.admin", new DateTime(2026, 9, 30, 8, 10, 0, DateTimeKind.Local), "demo.editor", DateTime.UtcNow));
        Assert.Throws<ArgumentException>(() => new Authorship("Demo Admin", DateTime.UtcNow, "demo.editor", DateTime.UtcNow));
        Assert.Throws<ArgumentException>(() => FieldValue.OfText(""));
        Assert.Throws<ArgumentOutOfRangeException>(() => FieldValue.OfNumber(double.NaN));
    }

    [Fact]
    public void AnotherDocument_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => RecordFile.TryRead(StashKey, Content(Record()), out _, out _));
        Assert.Throws<ArgumentException>(() => RecordFile.TryRead(RecordKey.InPrivate("demo.user"), Content(Record()), out _, out _));
        Assert.Throws<ArgumentException>(() => StashFile.TryRead(RecordKey, Content(Record()), out _, out _));
    }

    [Fact]
    public void StashFile_ReadsWhatItWrites_ThroughTheDataLayer()
    {
        using var data = new TestData();
        var stash = new StashFile(TestRecords.StashId, "Prod среда", TestRecords.Authors);

        var document = data.WriteAndRead(stash.Key, stash.ToContent());

        Assert.True(StashFile.TryRead(document, out var read, out var invalid), invalid);
        Assert.Equal((TestRecords.StashId, "Prod среда", "demo.admin", TestRecords.Authors.UpdatedAt), (read.Id, read.Name, read.Authorship.CreatedBy, read.Authorship.UpdatedAt));
        Assert.Equal(stash.ToContent().ToJsonString(), read.ToContent().ToJsonString());
    }

    [Theory]
    [MemberData(nameof(StashesThatDoNotFit))]
    public void AStashThatDoesNotFit_IsInvalid_WithAReason(string json, string reason)
    {
        Assert.False(StashFile.TryRead(StashKey, Content(json), out var stash, out var invalid));
        Assert.Null(stash);
        Assert.Contains(reason, invalid, StringComparison.Ordinal);
    }

    /// <summary>A record that fits, but for the part given; an empty part leaves its member out.</summary>
    private static string Record(
        string template = "\"template\":\"db\"",
        string dates = Dates,
        string stash = "\"stash\":[\"a1b2c3d4\"]",
        string name = "\"name\":\"Payments database\"",
        string tags = "\"tags\":[]",
        string publicFields = "",
        string groups = "\"protected\":{},\"fields\":{}")
    {
        static string Join(params string[] parts) => string.Join(",", parts.Where(part => part.Length > 0));
        return "{" + Join(template, dates, "\"public\":{" + Join(stash, name, publicFields, tags) + "}", groups) + "}";
    }

    private static JsonObject Content(string json) => JsonNode.Parse(json)!.AsObject();
}

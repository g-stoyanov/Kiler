using System.Globalization;
using System.Text.Json.Nodes;
using Kiler.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Core.Tests;

/// <summary>
/// A new record and a new stash (M5.4): a record reads back as it was written, each field at its level and each group
/// encrypted for its level, the values checked by their types, the path, the levels above the author refused, and a new id
/// for one that meets <see cref="WriteOutcome.Exists"/>.
/// </summary>
public class RecordActionsTests : IDisposable
{
    /// <summary>The stashes that the tests write, besides <see cref="TestRecords.StashId"/>.</summary>
    private const string Prod = "e5f6g7h8";

    /// <summary>A record of the template <c>note</c>, which a <c>ref</c> to a <c>db</c> does not take.</summary>
    private const string NoteId = "m4n5p6q7";

    private static readonly string[] MoreStashes = ["j9k0m1n2", "p3q4r5s6", "t7v8w9x0"];

    private static readonly Dictionary<string, FieldValue?> NoFields = [];

    private readonly TestRecords _kiler = new();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 9, 15, 30, 250, TimeSpan.Zero));

    private readonly Records _records;

    public RecordActionsTests()
    {
        // A template with a field of each type of design 6.4, beside the initial four.
        var every = new TemplateFile("every", "Every type", "note",
        [
            new("env", "Environment", FieldType.Select, level: 0, options: ["prod", "test", "dev"]),
            new("title", "Title", FieldType.Text, level: 0),
            new("lines", "Lines", FieldType.Multiline, level: 1),
            new("notes", "Notes", FieldType.Markdown, level: 1),
            new("manual", "Manual", FieldType.Doc, level: 1),
            new("count", "Count", FieldType.Number, level: 1),
            new("since", "Since", FieldType.Date, level: 1),
            new("site", "Site", FieldType.Url, level: 0),
            new("script", "Script", FieldType.Code, level: 1, languages: ["bash", "sql"]),
            new("query", "Query", FieldType.Code, level: 2, language: "sql"),
            new("db", "Database", FieldType.Ref, level: 1, refTemplate: "db"),
            new("password", "Password", FieldType.Secret, levelByEnv: new Dictionary<string, int> { ["prod"] = 3, ["test"] = 2, ["dev"] = 1 }),
        ]);
        _kiler.Data.WriteAndRead(every.Key, every.ToContent());
        _kiler.Team.Reload();

        _kiler.WriteStash(TestRecords.StashId, "Payments");
        _kiler.WriteStash(Prod, "Prod");
        foreach (var id in MoreStashes)
        {
            _kiler.WriteStash(id, $"Stash {id}");
        }

        Assert.Equal(WriteOutcome.Written, _kiler.Data.Copy.Create(StashFile.KeyOf("c6d7e8f9"), new JsonObject { ["name"] = "Broken" }).Outcome);
        _kiler.Write(_kiler.Database());
        _kiler.Write(_kiler.Record(NoteId, template: "note", groups: [(1, """{"text":"A note"}""")]));
        _records = _kiler.Open();
    }

    public void Dispose()
    {
        _records.Dispose();
        _kiler.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ARecord_ReadsBackAsItWasWritten_WithAFieldOfEachType()
    {
        var author = _kiler.Reader(3);
        var draft = Draft(
            "every",
            new Dictionary<string, FieldValue?>
            {
                ["env"] = FieldValue.OfText("test"),
                ["title"] = FieldValue.OfText("Reports server"),
                ["lines"] = FieldValue.OfText("first line\nsecond line"),
                ["notes"] = FieldValue.OfText("**Bold** note"),
                ["manual"] = FieldValue.OfText("manual.docx"),
                ["count"] = FieldValue.OfNumber(12.5),
                ["since"] = FieldValue.OfText("2026-10-21"),
                ["site"] = FieldValue.OfText("https://reports.example.test/start"),
                ["script"] = FieldValue.OfCode("bash", "echo ready"),
                ["query"] = FieldValue.OfText("select 1 from dual"),
                ["db"] = FieldValue.OfText(TestRecords.Id),
                ["password"] = FieldValue.OfText(TestRecords.Password),
            },
            stash: [TestRecords.StashId, Prod],
            name: "  Reports server  ",
            description: "Кратко описание",
            tags: [" reports ", "REPORTS", "", null, "отчети"]);

        var result = RecordActions.NewRecord(author, _records, _kiler.Team.Files, draft, _time);

        Assert.Equal((RecordOutcome.Done, null, null), (result.Outcome, result.Invalid, result.Field));
        var made = Assert.IsType<RecordView>(result.Record);
        Assert.Same(made, _records.Read(author).Records[made.File.Model!.Id]);

        // The file as the copy reads it again, with another Records over it.
        using var again = _kiler.Open();
        var view = again.Read(author).Records[made.File.Model.Id];
        var file = Assert.IsType<RecordFile>(view.File.Model);
        Assert.Equal((1, "every"), (view.File.Version, file.Template));
        Assert.Equal([TestRecords.StashId, Prod], file.Stash);
        Assert.Equal("Reports server", file.Name);
        Assert.Equal(["reports", "отчети"], file.Tags);
        var written = _time.GetUtcNow().UtcDateTime;
        Assert.Equal(("demo.user", written, "demo.user", written), (file.Authorship.CreatedBy, file.Authorship.CreatedAt, file.Authorship.UpdatedBy, file.Authorship.UpdatedAt));
        Assert.Equal(DateTimeKind.Utc, file.Authorship.CreatedAt.Kind);

        // Level 0 in clear, and each other level a group of its fields, listed in fields.
        Assert.Equal(["description", "env", "title", "site"], file.Public.Keys);
        Assert.Equal([1, 2], file.Protected.Keys);
        Assert.Equal(["lines", "notes", "manual", "count", "since", "script", "db"], file.Fields[1]);
        Assert.Equal(["query", "password"], file.Fields[2]);
        Assert.True(
            _kiler.Open(file, 2) == $$$"""{"query":"select 1 from dual","password":{"secret":"{{{TestRecords.Password}}}"}}""",
            "The group of level 2 is not the query and the password marked as a secret.");
        var text = File.ReadAllText(Path.Combine(_kiler.Data.Folder, DataFolder.Records, $"{file.Id}.json"));
        Assert.False(text.Contains(TestRecords.Password, StringComparison.Ordinal), "The password is in clear in the file.");
        Assert.Contains("Кратко описание", text, StringComparison.Ordinal);

        Assert.Equal((FieldKind.Value, 0, "Кратко описание"), (view.Description?.Kind, view.Description?.Level, view.Description?.Value?.Text));
        Assert.Equal(
            [
                ("env", 0, FieldKind.Value, "test"),
                ("title", 0, FieldKind.Value, "Reports server"),
                ("lines", 1, FieldKind.Value, "first line\nsecond line"),
                ("notes", 1, FieldKind.Value, "**Bold** note"),
                ("manual", 1, FieldKind.Value, "manual.docx"),
                ("count", 1, FieldKind.Value, "12.5"),
                ("since", 1, FieldKind.Value, "2026-10-21"),
                ("site", 0, FieldKind.Value, "https://reports.example.test/start"),
                ("script", 1, FieldKind.Value, "bash: echo ready"),
                ("query", 2, FieldKind.Value, "select 1 from dual"),
                ("db", 1, FieldKind.Value, TestRecords.Id),
                ("password", 2, FieldKind.Secret, null),
            ],
            view.Fields.Select(field => (field.Key, field.Level, field.Kind, Show(field.Value))));
    }

    [Theory]
    [InlineData("prod", 3)]
    [InlineData("test", 2)]
    [InlineData("dev", 1)]
    [InlineData(null, 3)]
    public void LevelByEnv_PutsTheUserAndThePassword_AtTheLevelOfTheEnvironment(string? env, int level)
    {
        var fields = new Dictionary<string, FieldValue?>
        {
            ["host"] = FieldValue.OfText("db01.example.test"),
            ["user"] = FieldValue.OfText("pay_read"),
            ["password"] = FieldValue.OfText(TestRecords.Password),
        };
        if (env is not null)
        {
            fields["env"] = FieldValue.OfText(env);
        }

        var file = Written(RecordActions.NewRecord(_kiler.Reader(3), _records, _kiler.Team.Files, Draft("db", fields), _time));

        int[] levels = level == 1 ? [1] : [1, level];
        string[] keys = level == 1 ? ["host", "user", "password"] : ["user", "password"];
        Assert.Equal(levels, file.Protected.Keys);
        Assert.Equal(keys, file.Fields[level]);
        var password = _records.Read(_kiler.Reader(3)).Records[file.Id].Fields.Single(field => field.Key == "password");
        Assert.Equal((level, FieldKind.Secret), (password.Level, password.Kind));
    }

    [Fact]
    public void ARaisedDescription_IsInTheGroupOfItsLevel_AndALowerLevelAsked_KeepsTheDefault()
    {
        var draft = Draft(
            "db",
            new Dictionary<string, FieldValue?> { ["host"] = FieldValue.OfText("db01.example.test") },
            new Dictionary<string, int> { ["description"] = 2, ["host"] = 0 },
            description: "Raised");

        var file = Written(RecordActions.NewRecord(_kiler.Reader(2), _records, _kiler.Team.Files, draft, _time));

        Assert.DoesNotContain("description", file.Public.Keys);
        Assert.Equal(["host"], file.Fields[1]);
        Assert.Equal(["description"], file.Fields[2]);
        Assert.Equal((FieldKind.Locked, 2), (Read(1, file.Id).Description?.Kind, Read(1, file.Id).Description?.Level));
        Assert.Equal("Raised", Read(2, file.Id).Description?.Value?.Text);
    }

    [Theory]
    [InlineData("prod", null, "password")]
    [InlineData("dev", "{\"description\":2}", "description")]
    [InlineData("dev", "{\"host\":3}", "host")]
    public void AFieldAboveTheAuthorsLevel_IsRefused(string env, string? levels, string field)
    {
        var fields = new Dictionary<string, FieldValue?>
        {
            ["env"] = FieldValue.OfText(env),
            ["host"] = FieldValue.OfText("db01.example.test"),
            ["password"] = FieldValue.OfText(TestRecords.Password),
        };

        var result = RecordActions.NewRecord(_kiler.Reader(1), _records, _kiler.Team.Files, Draft("db", fields, Levels(levels), description: "Raised"), _time);

        Assert.Equal((RecordOutcome.Invalid, field), (result.Outcome, result.Field));
        Assert.Null(result.Record);
        Assert.Equal(2, _records.Read(null).Records.Count);
    }

    [Fact]
    public void AFieldWithoutAValue_AtALevelAboveTheAuthor_DoesNotCount()
    {
        var fields = new Dictionary<string, FieldValue?> { ["env"] = FieldValue.OfText("prod"), ["host"] = FieldValue.OfText("db01.example.test"), ["password"] = null };

        var file = Written(RecordActions.NewRecord(_kiler.Reader(1), _records, _kiler.Team.Files, Draft("db", fields, Levels("{\"user\":3}")), _time));

        Assert.Equal([1], file.Protected.Keys);
    }

    [Theory]
    [InlineData("", "The record needs a stash.")]
    [InlineData("a1b2c3d4,,e5f6g7h8", "The path has a gap.")]
    [InlineData("a1b2c3d4, ", "The path has a gap.")]
    [InlineData("a1b2c3d4,e5f6g7h8,a1b2c3d4", "The path has a stash twice.")]
    [InlineData("a1b2c3d4,e5f6g7h8,j9k0m1n2,p3q4r5s6,t7v8w9x0", "The path has more than 4 stashes.")]
    [InlineData("a1b2c3d4,y2z3a4b5", "A stash of the path is not there.")]
    [InlineData("Payments", "A stash of the path is not there.")]
    [InlineData("c6d7e8f9", "A stash of the path is not there.")]
    public void APath_ThatDoesNotFit_IsRefused(string path, string invalid)
    {
        IReadOnlyList<string?> stash = path.Length == 0 ? [] : [.. path.Split(',').Select(id => id.Length == 0 ? null : id)];

        var result = RecordActions.NewRecord(_kiler.Reader(3), _records, _kiler.Team.Files, Draft("note", NoFields, stash: stash), _time);

        Assert.Equal((RecordOutcome.Invalid, "stash", invalid), (result.Outcome, result.Field, result.Invalid));
    }

    [Fact]
    public void APathOfFourStashes_IsWritten()
    {
        string[] path = [TestRecords.StashId, Prod, .. MoreStashes[..2]];

        var file = Written(RecordActions.NewRecord(_kiler.Reader(3), _records, _kiler.Team.Files, Draft("note", NoFields, stash: path), _time));

        Assert.Equal(path, file.Stash);
    }

    [Theory]
    [InlineData("env", "text", "staging")]
    [InlineData("env", "number", "1")]
    [InlineData("title", "number", "12")]
    [InlineData("title", "code", "sql:select 1")]
    [InlineData("count", "text", "12")]
    [InlineData("since", "text", "2026-13-01")]
    [InlineData("since", "text", "21.10.2026")]
    [InlineData("since", "text", "2026-10-21T10:00:00")]
    [InlineData("site", "text", "ftp://files.example.test")]
    [InlineData("site", "text", "reports.example.test/start")]
    [InlineData("site", "text", "javascript:alert(1)")]
    [InlineData("script", "text", "echo ready")]
    [InlineData("script", "code", "python:print(1)")]
    [InlineData("query", "code", "sql:select 1")]
    [InlineData("db", "text", "y2z3a4b5")]
    [InlineData("db", "text", NoteId)]
    [InlineData("db", "text", "not an id")]
    [InlineData("password", "number", "1234")]
    public void AValueThatDoesNotFitItsType_IsRefused(string key, string kind, string value)
    {
        var fields = new Dictionary<string, FieldValue?> { [key] = Value(kind, value) };

        var result = RecordActions.NewRecord(_kiler.Reader(3), _records, _kiler.Team.Files, Draft("every", fields), _time);

        Assert.Equal((RecordOutcome.Invalid, key), (result.Outcome, result.Field));
        Assert.NotNull(result.Invalid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARequiredField_LeftOut_IsRefused(bool asNull)
    {
        var fields = new Dictionary<string, FieldValue?> { ["kind"] = FieldValue.OfText("logs") };
        if (asNull)
        {
            fields["url"] = null;
        }

        var result = RecordActions.NewRecord(_kiler.Reader(3), _records, _kiler.Team.Files, Draft("link", fields), _time);

        Assert.Equal((RecordOutcome.Invalid, "url", "Address needs a value."), (result.Outcome, result.Field, result.Invalid));
    }

    [Theory]
    [InlineData("gone", null, "{}", null)]
    [InlineData(null, null, "{}", null)]
    [InlineData("note", "  ", "{}", "name")]
    [InlineData("note", null, "{}", "name")]
    [InlineData("note", "A note", "{\"stash\":0}", "stash")]
    [InlineData("note", "A note", "{\"tags\":1}", "tags")]
    [InlineData("db", "A database", "{\"env\":1}", "env")]
    [InlineData("note", "A note", "{\"createdBy\":1}", null)]
    [InlineData("note", "A note", "{\"text\":10}", "text")]
    [InlineData("note", "A note", "{\"text\":-1}", "text")]
    public void ARequestThatDoesNotFit_IsRefused(string? template, string? name, string levels, string? field)
    {
        var draft = Draft(template!, NoFields, Levels(levels)) with { Template = template, Name = name };

        var result = RecordActions.NewRecord(_kiler.Reader(3), _records, _kiler.Team.Files, draft, _time);

        Assert.Equal((RecordOutcome.Invalid, field), (result.Outcome, result.Field));
    }

    [Fact]
    public void AFieldThatTheTemplateDoesNotHave_IsRefused()
    {
        var fields = new Dictionary<string, FieldValue?> { ["host"] = FieldValue.OfText("db01.example.test") };

        var result = RecordActions.NewRecord(_kiler.Reader(3), _records, _kiler.Team.Files, Draft("note", fields), _time);

        Assert.Equal((RecordOutcome.Invalid, null, "The template has no field of this key."), (result.Outcome, result.Field, result.Invalid));
    }

    [Fact]
    public void ARecordTooLargeForADocument_IsRefused()
    {
        var fields = new Dictionary<string, FieldValue?> { ["text"] = FieldValue.OfText(new string('x', 800_000)) };

        var result = RecordActions.NewRecord(_kiler.Reader(3), _records, _kiler.Team.Files, Draft("note", fields), _time);

        Assert.Equal((RecordOutcome.Invalid, null, "The record is too large to be written."), (result.Outcome, result.Field, result.Invalid));
    }

    [Fact]
    public void AnIdThatMeetsExists_TakesANewOne()
    {
        var author = _kiler.Reader(3);
        var draft = Draft("note", new Dictionary<string, FieldValue?> { ["text"] = FieldValue.OfText("A new note") });
        var ids = new Queue<string>([TestRecords.Id, NoteId, "r8s9t0v1"]);

        var result = RecordActions.NewRecord(author, _records, _kiler.Team.Files, draft, _time, ids.Dequeue);

        // The groups are encrypted for the id that is written, so the record reads.
        var file = Written(result);
        Assert.Equal("r8s9t0v1", file.Id);
        Assert.Equal("A new note", _records.Read(author).Records[file.Id].Fields[0].Value?.Text);
        Assert.Equal("db", _records.Read(author).Records[TestRecords.Id].File.Model?.Template);
        Assert.Equal(RecordOutcome.NotWritten, RecordActions.NewRecord(author, _records, _kiler.Team.Files, draft, _time, () => TestRecords.Id).Outcome);
    }

    [Fact]
    public void ALockedAuthor_WritesNothing()
    {
        var author = _kiler.Reader(3);
        author.Dispose();

        Assert.Throws<SessionLockedException>(() => RecordActions.NewRecord(author, _records, _kiler.Team.Files, Draft("note", NoFields), _time));
        Assert.Throws<SessionLockedException>(() => RecordActions.NewStash(author, _records, "Reports", _time));
        Assert.Equal(2, _records.Read(null).Records.Count);
    }

    [Fact]
    public void ANewStash_IsWritten_WithItsNameTrimmed()
    {
        var author = _kiler.Reader(1);

        var result = RecordActions.NewStash(author, _records, "  Reports  ", _time, new Queue<string>([TestRecords.StashId, "r8s9t0v1"]).Dequeue);

        Assert.Equal(RecordOutcome.Done, result.Outcome);
        var stash = Assert.IsType<StashFile>(result.Stash);
        Assert.Equal(("r8s9t0v1", "Reports", "demo.user", _time.GetUtcNow().UtcDateTime), (stash.Id, stash.Name, stash.Authorship.CreatedBy, stash.Authorship.CreatedAt));
        Assert.Same(stash, _records.Read(author).Stashes["r8s9t0v1"].Model);
        using var again = _kiler.Open();
        Assert.Equal("Reports", again.Read(null).Stashes["r8s9t0v1"].Model?.Name);
        Assert.Equal("Payments", again.Read(null).Stashes[TestRecords.StashId].Model?.Name);
    }

    [Theory]
    [InlineData("PAYMENTS", RecordOutcome.Exists, null)]
    [InlineData(" prod ", RecordOutcome.Exists, null)]
    [InlineData("ПЛАЩАНИЯ", RecordOutcome.Exists, null)]
    [InlineData("   ", RecordOutcome.Invalid, "name")]
    [InlineData(null, RecordOutcome.Invalid, "name")]
    public void ANewStash_WhoseNameIsThereInAnotherCase_OrIsBlank_IsRefused(string? name, RecordOutcome outcome, string? field)
    {
        var author = _kiler.Reader(1);
        Assert.Equal(RecordOutcome.Done, RecordActions.NewStash(author, _records, "Плащания", _time).Outcome);

        var result = RecordActions.NewStash(author, _records, name, _time);

        Assert.Equal((outcome, field, null), (result.Outcome, result.Field, result.Stash));
        Assert.Equal(7, _records.Read(author).Stashes.Count);
    }

    /// <summary>A draft of <paramref name="template"/> with a name and the stash <see cref="TestRecords.StashId"/>, unless given others.</summary>
    private static RecordDraft Draft(
        string template,
        IReadOnlyDictionary<string, FieldValue?> fields,
        IReadOnlyDictionary<string, int>? levels = null,
        IReadOnlyList<string?>? stash = null,
        string? name = "Payments database",
        string? description = null,
        IReadOnlyList<string?>? tags = null) =>
        new(template, stash ?? [TestRecords.StashId], name, description, tags ?? [], fields, levels ?? new Dictionary<string, int>());

    /// <summary>The levels asked, from their JSON, or none.</summary>
    private static Dictionary<string, int> Levels(string? json) =>
        json is null ? [] : JsonNode.Parse(json)!.AsObject().ToDictionary(level => level.Key, level => level.Value!.GetValue<int>());

    /// <summary>A value of <paramref name="kind"/>: a text, a number, or a code as <c>language:code</c>.</summary>
    private static FieldValue Value(string kind, string value) => kind switch
    {
        "number" => FieldValue.OfNumber(double.Parse(value, CultureInfo.InvariantCulture)),
        "code" => FieldValue.OfCode(value[..value.IndexOf(':', StringComparison.Ordinal)], value[(value.IndexOf(':', StringComparison.Ordinal) + 1)..]),
        _ => FieldValue.OfText(value),
    };

    /// <summary>A value as a text, to compare: a number in the invariant culture, a code as <c>language: code</c>.</summary>
    private static string? Show(FieldValue? value) => value switch
    {
        { Kind: FieldValueKind.Number } => value.Number.ToString(CultureInfo.InvariantCulture),
        { Kind: FieldValueKind.Code } => $"{value.Language}: {value.Text}",
        _ => value?.Text,
    };

    /// <summary>The file of the record that <paramref name="result"/> wrote.</summary>
    private static RecordFile Written(RecordResult result)
    {
        Assert.Equal((RecordOutcome.Done, null), (result.Outcome, result.Invalid));
        return Assert.IsType<RecordFile>(result.Record?.File.Model);
    }

    /// <summary>The record <paramref name="id"/> as a reader of <paramref name="level"/> reads it.</summary>
    private RecordView Read(int level, string id) => _records.Read(_kiler.Reader(level)).Records[id];
}

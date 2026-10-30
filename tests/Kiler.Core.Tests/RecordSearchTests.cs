using System.Text.Json.Nodes;
using Kiler.Storage;

namespace Kiler.Core.Tests;

/// <summary>
/// The search, the filters, the tree and the facets of the list (M5.6, docs/api.md, <c>GET /api/records</c>) over a made-up
/// Kiler of three levels: words in Cyrillic and in Latin in any case, a value that only a session of its level finds or
/// counts, a <c>secret</c> that is never found or counted, and the changes of the share followed.
/// </summary>
public class RecordSearchTests : IDisposable
{
    // The stashes: Плащания (payments), Prod and Reports, and a stash that a path names and no file has.
    private const string Payments = "a1b2c3d4";
    private const string Prod = "e5f6g7h8";
    private const string Reports = "j9k0m1n2";
    private const string NoStash = "p3q4r5s6";

    // The records.
    private const string ProdDb = TestRecords.Id;
    private const string TestDb = "m4n5p6q7";
    private const string Monitoring = "r8s9t0v1";
    private const string Notes = "t2v3w4x5";
    private const string Orphan = "x6y7z8a9";
    private const string Tool = "b1c2d3e4";
    private const string Broken = "f5g6h7j8";

    private readonly TestRecords _kiler = new();

    public RecordSearchTests()
    {
        foreach (var (id, name) in new[] { (Payments, "Плащания"), (Prod, "Prod"), (Reports, "Reports") })
        {
            _kiler.WriteStash(id, name);
        }

        // A template of the test, with a field of each kind of text for the facets: owner at level 1, so a guest neither
        // finds nor counts it, and token a text that its group marks as a secret, as when the template on the share was
        // changed by hand.
        var tool = new TemplateFile("tool", "Tool", "code",
        [
            new("owner", "Owner", FieldType.Text, level: 1, filter: true, list: true),
            new("port", "Port", FieldType.Number, level: 0, filter: true),
            new("db", "Database", FieldType.Ref, level: 0, filter: true, refTemplate: "db"),
            new("snippet", "Snippet", FieldType.Code, level: 0, filter: true, languages: ["bash", "sql"]),
            new("token", "Token", FieldType.Text, level: 1, filter: true, list: true),
        ]);
        Assert.Equal(WriteOutcome.Written, _kiler.Data.Copy.Create(tool.Key, tool.ToContent()).Outcome);
        _kiler.Team.Reload();

        _kiler.Write(Record(ProdDb, "db", "Payments prod database", [Payments, Prod], ["payments", "Oracle"],
            new() { ["env"] = FieldValue.OfText("prod"), ["description"] = FieldValue.OfText("Главната база на плащанията") },
            (1, """{"host":"db01.example.test","port":1521}"""),
            (3, $$$"""{"user":"pay_read","password":{"secret":"{{{TestRecords.Password}}}"}}""")));
        // The password in public, where the template says secret, as only a file changed by hand has it (M5.3).
        _kiler.Write(Record(TestDb, "db", "Payments test database", [Payments], ["payments"],
            new() { ["env"] = FieldValue.OfText("test"), ["password"] = FieldValue.OfText(TestRecords.Password) },
            (1, """{"host":"db02.example.test"}""")));
        _kiler.Write(Record(Monitoring, "link", "Monitoring", [Payments, Prod], ["monitoring"],
            new() { ["url"] = FieldValue.OfText("https://monitoring.example.test"), ["kind"] = FieldValue.OfText("monitoring") }));
        _kiler.Write(Record(Notes, "note", "Бележки за отчетите", [Reports], ["PAYMENTS"],
            new(),
            (1, """{"text":"Месечен отчет"}""")));
        _kiler.Write(Record(Orphan, "gone", "Orphan", [NoStash], [],
            new() { ["description"] = FieldValue.OfText("Its template is not there") }));

        _kiler.Write(Record(Tool, "tool", "Deploy tool", [Reports], [],
            new() { ["port"] = FieldValue.OfNumber(8080), ["db"] = FieldValue.OfText(ProdDb), ["snippet"] = FieldValue.OfCode("bash", "echo deploy") },
            (1, $$$"""{"owner":"team-a","token":{"secret":"{{{TestRecords.Password}}}"}}""")));
        Assert.Equal(WriteOutcome.Written, _kiler.Data.Copy.Create(RecordFile.KeyOf(Broken), new JsonObject { ["template"] = "db" }).Outcome);
    }

    public void Dispose()
    {
        _kiler.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("payments PROD", new[] { Tool, ProdDb })]
    [InlineData("ПЛАЩАНИЯ", new[] { Monitoring, ProdDb, TestDb })]
    [InlineData("главната", new[] { ProdDb })]
    [InlineData("  отчет \t", new[] { Notes })]
    [InlineData("oracle", new[] { ProdDb })]
    [InlineData("reports", new[] { Tool, Notes })]
    [InlineData("orphan", new[] { Orphan })]
    [InlineData("db01.example", new[] { ProdDb })]
    [InlineData("echo 8080", new[] { Tool })]
    [InlineData("payments missing", new string[0])]
    public void EveryWord_IsFound_InCyrillicAndInLatin_InAnyCase(string text, string[] ids)
    {
        using var records = _kiler.Open();

        var found = Find(records, _kiler.Reader(3), new RecordQuery { Text = text });

        Assert.Equal(ids, found.Records.Select(view => view.File.Model!.Id));
    }

    [Theory]
    [InlineData(0, new[] { Monitoring })]
    [InlineData(1, new[] { Monitoring, ProdDb, TestDb })]
    public void AFieldAboveTheLevel_IsNotFound(int level, string[] ids)
    {
        using var records = _kiler.Open();

        // The address of the monitoring is at level 0, and the hosts of the databases at 1.
        var found = Find(records, level == 0 ? null : _kiler.Reader(level), new RecordQuery { Text = "example.test" });

        Assert.Equal(ids, found.Records.Select(view => view.File.Model!.Id));
    }

    [Fact]
    public void APassword_IsNeitherFound_NorCounted_WhereverItIs()
    {
        using var records = _kiler.Open();

        var found = Find(records, _kiler.Reader(3), new RecordQuery { Text = TestRecords.Password });
        var all = Find(records, _kiler.Reader(3), new RecordQuery());

        Assert.Empty(found.Records);
        Assert.DoesNotContain("token", all.Fields.Select(field => field.Key));
        Assert.True(
            all.Fields.SelectMany(field => field.Values).All(value => !value.Value.Contains(TestRecords.Password, StringComparison.Ordinal)),
            "A facet counts a secret.");
    }

    [Theory]
    [InlineData(new[] { "payments" }, new[] { ProdDb, TestDb, Notes })]
    [InlineData(new[] { "PAYMENTS", "oracle" }, new[] { ProdDb })]
    [InlineData(new[] { "payments", "monitoring" }, new string[0])]
    public void TheTags_AreEachNeeded_InAnyCase(string[] tags, string[] ids)
    {
        using var records = _kiler.Open();

        var found = Find(records, _kiler.Reader(3), new RecordQuery { Tags = tags });

        Assert.Equal(ids.Order(StringComparer.Ordinal), found.Records.Select(view => view.File.Model!.Id).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(1, "env", "PROD", new[] { ProdDb })]
    [InlineData(1, "kind", "Monitoring", new[] { Monitoring })]
    [InlineData(1, "owner", "team-a", new[] { Tool })]
    [InlineData(0, "owner", "team-a", new string[0])]
    [InlineData(1, "port", "8080", new[] { Tool })]
    [InlineData(1, "db", "payments prod DATABASE", new[] { Tool })]
    [InlineData(1, "snippet", "echo deploy", new[] { Tool })]
    [InlineData(3, "token", TestRecords.Password, new string[0])]
    [InlineData(3, "password", TestRecords.Password, new string[0])]
    [InlineData(3, "host", "db01.example.test", new string[0])]
    public void AFieldFilter_MatchesTheTextThatTheSessionReads_OfAFieldWithFilter(int level, string key, string value, string[] ids)
    {
        using var records = _kiler.Open();

        var found = Find(records, level == 0 ? null : _kiler.Reader(level), new RecordQuery { Fields = new Dictionary<string, string> { [key] = value } });

        Assert.Equal(ids, found.Records.Select(view => view.File.Model!.Id));
    }

    [Fact]
    public void TheTree_KeepsEveryBranch_WhileANodeIsChosen_AndCountsTheRecordsUnderEachNode()
    {
        using var records = _kiler.Open();

        var found = Find(records, _kiler.Reader(3), new RecordQuery { Stash = [Payments] });

        Assert.Equal(["Monitoring", "Payments prod database", "Payments test database"], found.Records.Select(view => view.File.Model!.Name));

        // By name without regard to case, a stash that cannot be read last; a record lives at the last stash of its path.
        Assert.Equal(
            ["Reports 2 []", "Плащания 3 [Prod 2 []]", "- 1 []"],
            found.Tree.Select(Describe));
        Assert.Equal([Reports, Payments, NoStash], found.Tree.Select(node => node.Id));

        // Under a deeper node, with a word: the tree counts only the records that match it.
        var deeper = Find(records, _kiler.Reader(3), new RecordQuery { Text = "payments", Stash = [Payments, Prod] });
        Assert.Equal([ProdDb], deeper.Records.Select(view => view.File.Model!.Id));
        Assert.Equal(["Reports 2 []", "Плащания 2 [Prod 1 []]"], deeper.Tree.Select(Describe));
    }

    [Fact]
    public void TheFacets_CountEachChoice_WithoutTheirOwnFilter_AndTheTags_WithTheTagsChosen()
    {
        using var records = _kiler.Open();

        var found = Find(records, _kiler.Reader(3), new RecordQuery { Template = "db", Tags = ["payments"], Fields = new Dictionary<string, string> { ["env"] = "prod" } });

        Assert.Equal([ProdDb], found.Records.Select(view => view.File.Model!.Id));
        Assert.Equal(["db 1"], found.Templates.Select(Describe));
        Assert.Equal(["Oracle 1", "payments 1"], found.Tags.Select(Describe));
        Assert.Equal(["env: prod 1, test 1"], found.Fields.Select(Describe));

        // Without the field filter, the note with the tag counts under its template, which is not the one chosen. Each field
        // with filter that the session reads gives its text: a number as JSON writes it, the code without its language, a
        // ref by the name of its record.
        var tagged = Find(records, _kiler.Reader(1), new RecordQuery { Template = "db", Tags = ["payments"] });
        Assert.Equal([ProdDb, TestDb], tagged.Records.Select(view => view.File.Model!.Id));
        Assert.Equal(["db 2", "note 1"], tagged.Templates.Select(Describe));
        Assert.Equal(["Oracle 1", "payments 2"], tagged.Tags.Select(Describe));
        var all = Find(records, _kiler.Reader(1), new RecordQuery());
        Assert.Equal(
            ["db: Payments prod database 1", "env: prod 1, test 1", "kind: monitoring 1", "owner: team-a 1", "port: 8080 1", "snippet: echo deploy 1"],
            all.Fields.Select(Describe));
        Assert.DoesNotContain("owner", Find(records, null, new RecordQuery()).Fields.Select(field => field.Key));
    }

    [Fact]
    public void ARecordThatDoesNotFit_IsLeftOut_AndOneWithoutItsTemplate_HasItsHeader()
    {
        using var records = _kiler.Open();

        var found = Find(records, _kiler.Reader(3), new RecordQuery());

        Assert.Equal(
            ["Deploy tool", "Monitoring", "Orphan", "Payments prod database", "Payments test database", "Бележки за отчетите"],
            found.Records.Select(view => view.File.Model!.Name));
        Assert.Equal(["db 2", "gone 1", "link 1", "note 1", "tool 1"], found.Templates.Select(Describe));
        Assert.Equal(6, found.Tree.Sum(node => node.Count));

        // Found by its description, and by no field filter.
        Assert.Equal([Orphan], Find(records, null, new RecordQuery { Text = "template" }).Records.Select(view => view.File.Model!.Id));
    }

    [Fact]
    public void TheSearch_FollowsTheChangesOfTheShare()
    {
        using var records = _kiler.Open();
        var session = _kiler.Reader(3);
        Assert.Equal([Tool, Notes], Find(records, session, new RecordQuery { Text = "reports" }).Records.Select(view => view.File.Model!.Id));

        // Another Kiler renames a stash and changes a record.
        var other = _kiler.Data.OpenCopy();
        other.Load();
        Assert.Equal(WriteOutcome.Written, other.Replace(StashFile.KeyOf(Reports), 1, new StashFile(Reports, "Отчети", TestRecords.Authors).ToContent()).Outcome);
        var changed = Record(Monitoring, "link", "Monitoring of the payments", [Payments, Prod], ["monitoring"], new() { ["kind"] = FieldValue.OfText("logs") });
        Assert.Equal(WriteOutcome.Written, other.Replace(changed.Key, 1, changed.ToContent()).Outcome);
        var load = _kiler.Data.Copy.Load();
        _kiler.Team.Follow(load);
        records.Follow(load);

        Assert.Empty(Find(records, session, new RecordQuery { Text = "reports" }).Records);
        Assert.Equal([Tool, Notes], Find(records, session, new RecordQuery { Text = "ОТЧЕТИ" }).Records.Select(view => view.File.Model!.Id));
        Assert.Equal([Monitoring], Find(records, session, new RecordQuery { Fields = new Dictionary<string, string> { ["kind"] = "logs" } }).Records.Select(view => view.File.Model!.Id));
    }

    private static SearchResult Find(Records records, Session? session, RecordQuery query) => RecordSearch.Find(records.Read(session), query);

    private static string Describe(StashNode node) => $"{node.Name ?? "-"} {node.Count} [{string.Join(", ", node.Children.Select(Describe))}]";

    private static string Describe(FacetValue value) => $"{value.Value} {value.Count}";

    private static string Describe(FieldFacet field) => $"{field.Key}: {string.Join(", ", field.Values.Select(Describe))}";

    /// <summary>A record whose groups are encrypted with the keys of their levels, their keys listed in <c>fields</c>.</summary>
    private RecordFile Record(string id, string template, string name, string[] stash, string[] tags, Dictionary<string, FieldValue> publicFields, params (int Level, string Json)[] groups) =>
        new(
            id,
            template,
            TestRecords.Authors,
            stash,
            name,
            tags,
            publicFields,
            groups.ToDictionary(group => group.Level, group => _kiler.Group(id, group.Level, group.Json)),
            groups.ToDictionary(group => group.Level, group => (IReadOnlyList<string>)[.. JsonNode.Parse(group.Json)!.AsObject().Select(member => member.Key)]));
}

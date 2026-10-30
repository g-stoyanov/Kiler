using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kiler.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Core.Tests;

/// <summary>
/// The edit of a record (M5.8): the partial edit of design 5.2 keeps the groups above the editor byte for byte, no field
/// moves across a level that the editor does not hold, a raise is kept, a <c>secret</c> left out keeps its value, a value
/// that the record has is not checked again, and an edit over another version is a conflict with the record as it is now.
/// </summary>
public class RecordEditTests : IDisposable
{
    /// <summary>A database of <c>dev</c>: the host and the password at 1.</summary>
    private const string DevId = "d3v4d5b6";

    /// <summary>A database of <c>prod</c> whose host and description an admin raised to 3.</summary>
    private const string RaisedId = "r4s5e6d7";

    /// <summary>A database whose <c>env</c> its template no longer allows.</summary>
    private const string StagingId = "s7t8g9h0";

    /// <summary>A query that runs in a database that has gone.</summary>
    private const string SqlId = "q1w2e3r4";

    /// <summary>A database whose group of level 2 was moved from another record.</summary>
    private const string MovedId = "b7r8k9n0";

    private const string NoTemplateId = "n0t1m2p3";

    private const string NotFitId = "f1t2s3n4";

    private readonly TestRecords _kiler = new();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 9, 15, 30, 250, TimeSpan.Zero));

    private readonly Records _records;

    public RecordEditTests()
    {
        _kiler.WriteStash(TestRecords.StashId, "Payments");
        _kiler.Write(_kiler.Database());
        _kiler.Write(_kiler.Record(DevId, publicFields: Env("dev"), groups: [(1, $$$"""{"host":"db03.example.test","password":{"secret":"{{{TestRecords.Password}}}"}}""")]));
        _kiler.Write(_kiler.Record(RaisedId, publicFields: Env("prod"), groups: [(1, """{"port":1521}"""), (3, """{"host":"db04.example.test","description":"Raised"}""")]));
        _kiler.Write(_kiler.Record(StagingId, publicFields: Env("staging"), groups: [(1, """{"host":"db05.example.test"}""")]));
        _kiler.Write(_kiler.Record(SqlId, template: "sql", groups: [(1, """{"db":"y2z3a4b5","query":"select 1 from dual"}""")]));
        _kiler.Write(new RecordFile(
            MovedId,
            "db",
            TestRecords.Authors,
            [TestRecords.StashId],
            "Moved group",
            [],
            Env("prod"),
            new Dictionary<int, string> { [2] = _kiler.Group(TestRecords.Id, 2, """{"host":"db06.example.test"}""") },
            new Dictionary<int, IReadOnlyList<string>> { [2] = ["host"] }));
        _kiler.Write(_kiler.Record(NoTemplateId, template: "gone"));
        Assert.Equal(WriteOutcome.Written, _kiler.Data.Copy.Create(RecordFile.KeyOf(NotFitId), new JsonObject { ["name"] = "Broken" }).Outcome);
        _records = _kiler.Open();
    }

    public void Dispose()
    {
        _records.Dispose();
        _kiler.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void APartialEdit_KeepsTheGroupAboveTheEditor_ByteForByte()
    {
        var before = Version1(TestRecords.Id);

        var result = Edit(2, TestRecords.Id, Draft("""{"env":"prod","host":"db02.example.test","port":1521}""", description: "The main database"));

        var file = Written(result);
        Assert.Equal(2, result.Record?.File.Version);
        Assert.Equal([1, 3], file.Protected.Keys);
        Assert.Equal(before.Protected[3], file.Protected[3]);
        Assert.Equal(["user", "password"], file.Fields[3]);
        Assert.Equal(["host", "port"], file.Fields[1]);

        // The file as the copy reads it again, with another Records over it, for a reader of every level.
        using var again = _kiler.Open();
        var reader = _kiler.Reader(3);
        var view = again.Read(reader).Records[TestRecords.Id];
        Assert.Equal(
            [
                ("env", 0, FieldKind.Value, "prod"),
                ("host", 1, FieldKind.Value, "db02.example.test"),
                ("port", 1, FieldKind.Value, "1521"),
                ("tns", 1, FieldKind.Empty, null),
                ("user", 3, FieldKind.Value, "pay_read"),
                ("password", 3, FieldKind.Secret, null),
            ],
            view.Fields.Select(field => (field.Key, field.Level, field.Kind, Show(field.Value))));
        Assert.Equal(SecretOutcome.Value, view.Secret("password", reader, out var value));
        Assert.True(value == TestRecords.Password, "The password is not the one that the record had.");
    }

    [Fact]
    public void AnEdit_KeepsTheCreation_AndTheEditorAndNowAreTheLastChange()
    {
        var file = Written(Edit(1, TestRecords.Id, Draft("""{"env":"prod","host":"db02.example.test"}""")));

        var authors = file.Authorship;
        Assert.Equal(
            ("demo.admin", TestRecords.Authors.CreatedAt, "demo.user", _time.GetUtcNow().UtcDateTime),
            (authors.CreatedBy, authors.CreatedAt, authors.UpdatedBy, authors.UpdatedAt));
        Assert.Equal(DateTimeKind.Utc, authors.UpdatedAt.Kind);
    }

    [Theory]
    [InlineData(DevId, """{"env":"prod","host":"db03.example.test"}""", null, "password", "Password would be at level 3, which you do not hold.")]
    [InlineData(TestRecords.Id, """{"env":"test"}""", null, "user", "User would move from level 3, which you do not hold, to level 2.")]
    [InlineData(DevId, """{"env":"dev","host":"db03.example.test"}""", """{"host":3}""", "host", "Host would be at level 3, which you do not hold.")]
    [InlineData(RaisedId, """{"env":"prod","port":1521}""", """{"host":1}""", "host", "Host would move from level 3, which you do not hold, to level 1.")]
    public void AFieldThatWouldMoveAcrossALevelThatTheEditorDoesNotHold_IsRefused(string id, string fields, string? levels, string field, string invalid)
    {
        var result = Edit(2, id, Draft(fields, levels));

        Assert.Equal((RecordOutcome.Invalid, field, invalid), (result.Outcome, result.Field, result.Invalid));
        Assert.Equal(1, _records.Read(null).Records[id].File.Version);
    }

    [Fact]
    public void ARaiseAndALowering_WithinTheEditorsLevels_MoveTheFields_AndARaiseIsKept()
    {
        // The environment takes the user and the password down from 3 to 1, and the host is raised to 2.
        const string Dev = """{"env":"dev","host":"db01.example.test","port":1521,"user":"pay_read"}""";
        var file = Written(Edit(3, TestRecords.Id, Draft(Dev, """{"host":2}""")));

        Assert.Equal([1, 2], file.Protected.Keys);
        Assert.Equal(["port", "user", "password"], file.Fields[1]);
        Assert.Equal(["host"], file.Fields[2]);
        Assert.True(
            _kiler.Open(file, 1) == $$$"""{"port":1521,"user":"pay_read","password":{"secret":"{{{TestRecords.Password}}}"}}""",
            "The group of level 1 does not keep the password as its group had it.");
        var reader = _kiler.Reader(1);
        Assert.Equal(SecretOutcome.Value, _records.Read(reader).Records[TestRecords.Id].Secret("password", reader, out var value));
        Assert.True(value == TestRecords.Password, "The password is not the one that the record had.");

        // Back to prod: the levels leave out the host, which keeps its raise, and the user and the password go up again.
        file = Written(Edit(3, TestRecords.Id, Draft(Dev.Replace("dev", "prod", StringComparison.Ordinal)), version: 2));
        Assert.Equal([["port"], ["host"], ["user", "password"]], file.Fields.Values);

        // A lower level asked takes the raise back to the default.
        file = Written(Edit(3, TestRecords.Id, Draft(Dev.Replace("dev", "prod", StringComparison.Ordinal), """{"host":0}"""), version: 3));
        Assert.Equal([["host", "port"], ["user", "password"]], file.Fields.Values);
    }

    [Fact]
    public void ASecretLeftOut_KeepsItsValue_OneGivenAsNullIsCleared_AndAnotherFieldLeftOut_HasNoValue()
    {
        var file = Written(Edit(3, TestRecords.Id, Draft("""{"env":"prod","host":"db01.example.test","user":"pay_read"}""")));

        Assert.Equal([["host"], ["user", "password"]], file.Fields.Values);
        var fields = Read(3, TestRecords.Id).Fields.ToDictionary(field => field.Key);
        Assert.Equal((FieldKind.Empty, FieldKind.Secret), (fields["port"].Kind, fields["password"].Kind));
        Assert.Equal(FieldKind.Empty, Read(3, TestRecords.Id).Description?.Kind);

        file = Written(Edit(3, TestRecords.Id, Draft("""{"env":"prod","host":"db01.example.test","user":"pay_read","password":null}"""), version: 2));

        Assert.Equal(["user"], file.Fields[3]);
        Assert.Equal(FieldKind.Empty, Read(3, TestRecords.Id).Fields.Single(field => field.Key == "password").Kind);
    }

    [Fact]
    public void AValueThatTheRecordHas_IsNotCheckedAgain()
    {
        Written(Edit(1, StagingId, Draft("""{"env":"staging","host":"db05.example.test"}""")));
        Written(Edit(1, SqlId, Draft("""{"db":"y2z3a4b5","query":"select 2 from dual"}""")));

        var env = Edit(1, StagingId, Draft("""{"env":"qa","host":"db05.example.test"}"""), version: 2);
        var db = Edit(1, SqlId, Draft("""{"db":"z9y8x7w6","query":"select 2 from dual"}"""), version: 2);

        Assert.Equal((RecordOutcome.Invalid, "env", "Environment must be one of its options."), (env.Outcome, env.Field, env.Invalid));
        Assert.Equal((RecordOutcome.Invalid, "db", "Runs in must point to a record of its template that is there."), (db.Outcome, db.Field, db.Invalid));
    }

    [Theory]
    [InlineData(TestRecords.Id, """{"env":"prod","password":"another-made-up-password"}""", null, "password", "Password is at level 3, which you do not hold.")]
    [InlineData(TestRecords.Id, """{"env":"prod","user":null}""", null, "user", "User is at level 3, which you do not hold.")]
    [InlineData(RaisedId, """{"env":"prod"}""", "Changed", "description", "The description is at level 3, which you do not hold.")]
    public void AFieldOfAGroupAboveTheEditor_InTheDraft_IsRefused(string id, string fields, string? description, string field, string invalid)
    {
        var result = Edit(2, id, Draft(fields, description: description));

        Assert.Equal((RecordOutcome.Invalid, field, invalid), (result.Outcome, result.Field, result.Invalid));
        Assert.Equal(1, _records.Read(null).Records[id].File.Version);
    }

    [Theory]
    [InlineData(1, RecordOutcome.Done)]
    [InlineData(2, RecordOutcome.Unreadable)]
    public void AGroupOfTheEditorThatDoesNotDecrypt_IsNotWrittenOver(int level, RecordOutcome outcome)
    {
        var before = Version1(MovedId);

        var result = Edit(level, MovedId, Draft("""{"env":"prod"}"""));

        Assert.Equal(outcome, result.Outcome);
        var file = _records.Read(null).Records[MovedId].File;
        Assert.Equal(before.Protected[2], file.Model?.Protected[2]);
        Assert.Equal(outcome == RecordOutcome.Done ? 2 : 1, file.Version);
    }

    [Theory]
    [InlineData("y2z3a4b5", RecordOutcome.Missing)]
    [InlineData(NotFitId, RecordOutcome.Missing)]
    [InlineData(NoTemplateId, RecordOutcome.NoTemplate)]
    public void ARecordThatIsNotThere_OrWhoseTemplateCannotBeUsed_IsNotEdited(string id, RecordOutcome outcome)
    {
        var result = Edit(3, id, Draft("{}"));

        Assert.Equal((outcome, null), (result.Outcome, result.Record));
    }

    [Fact]
    public void AnEditOverAnotherVersion_IsAConflict_WithTheRecordAsItIsNow()
    {
        var draft = Draft("""{"env":"prod","host":"db02.example.test"}""");
        Written(Edit(3, TestRecords.Id, draft));

        var stale = Edit(3, TestRecords.Id, draft);

        Assert.Equal((RecordOutcome.Conflict, 2), (stale.Outcome, stale.Record?.File.Version));

        // Another Kiler has written a version that this one has not loaded yet: the write meets it on the share.
        Assert.Equal(WriteOutcome.Written, _kiler.Data.OpenCopy().Replace(RecordFile.KeyOf(TestRecords.Id), 2, Elsewhere().ToContent()).Outcome);

        var conflict = Edit(3, TestRecords.Id, draft, version: 2);

        Assert.Equal((RecordOutcome.Conflict, 3, "Changed elsewhere"), (conflict.Outcome, conflict.Record?.File.Version, conflict.Record?.File.Model?.Name));
        Assert.Equal(2, _records.Read(null).Records[TestRecords.Id].File.Version);
        Follow(_kiler.Data.Copy.Load());
        Assert.Equal((3, "Changed elsewhere"), (_records.Read(null).Records[TestRecords.Id].File.Version, _records.Read(null).RecordName(TestRecords.Id)));
    }

    [Fact]
    public void ALoadThatEndedBeforeAnEdit_DoesNotTakeItsPlace()
    {
        Assert.Equal(WriteOutcome.Written, _kiler.Data.OpenCopy().Replace(RecordFile.KeyOf(TestRecords.Id), 1, Elsewhere().ToContent()).Outcome);

        // The watch raises a load after it has let go of the copy, so a write can come between its end and the records.
        var before = _kiler.Data.Copy.Load();
        Assert.Equal(WriteOutcome.Written, _records.Replace(_kiler.Database(), 2).Outcome);
        Follow(before);

        Assert.Equal((3, "Payments database"), (_records.Read(null).Records[TestRecords.Id].File.Version, _records.Read(null).RecordName(TestRecords.Id)));
    }

    [Fact]
    public void ALockedEditor_WritesNothing()
    {
        var editor = _kiler.Reader(3);
        editor.Dispose();

        Assert.Throws<SessionLockedException>(() => RecordActions.Edit(editor, _records, TestRecords.Id, 1, Draft("""{"env":"prod"}"""), _time));
        Assert.Equal(1, _records.Read(null).Records[TestRecords.Id].File.Version);
    }

    /// <summary>An edit with the stash <see cref="TestRecords.StashId"/>, the name and the tag of the records of <see cref="TestRecords"/>.</summary>
    private static RecordDraft Draft(string fields, string? levels = null, string? description = null) =>
        new(null, [TestRecords.StashId], "Payments database", description, ["payments"], Fields(fields), Levels(levels));

    /// <summary>The values of the fields from their JSON: a string a text, a number a number, and <c>null</c> as it is.</summary>
    private static Dictionary<string, FieldValue?> Fields(string json) =>
        JsonNode.Parse(json)!.AsObject().ToDictionary(
            field => field.Key,
            field => field.Value?.GetValueKind() switch
            {
                null => null,
                JsonValueKind.Number => FieldValue.OfNumber(field.Value.GetValue<double>()),
                _ => FieldValue.OfText(field.Value.GetValue<string>()),
            });

    /// <summary>The levels asked, from their JSON, or none.</summary>
    private static Dictionary<string, int> Levels(string? json) =>
        json is null ? [] : JsonNode.Parse(json)!.AsObject().ToDictionary(level => level.Key, level => level.Value!.GetValue<int>());

    private static Dictionary<string, FieldValue> Env(string env) => new() { ["env"] = FieldValue.OfText(env) };

    /// <summary>A value as a text, to compare: a number in the invariant culture.</summary>
    private static string? Show(FieldValue? value) => value is { Kind: FieldValueKind.Number } ? value.Number.ToString(CultureInfo.InvariantCulture) : value?.Text;

    /// <summary>The file of the record that <paramref name="result"/> wrote.</summary>
    private static RecordFile Written(RecordResult result)
    {
        Assert.Equal((RecordOutcome.Done, null, null), (result.Outcome, result.Field, result.Invalid));
        return Assert.IsType<RecordFile>(result.Record?.File.Model);
    }

    /// <summary>The database, renamed by another Kiler, without its groups.</summary>
    private static RecordFile Elsewhere() =>
        new(TestRecords.Id, "db", TestRecords.Authors, [TestRecords.StashId], "Changed elsewhere", [], Env("prod"), new Dictionary<int, string>(), new Dictionary<int, IReadOnlyList<string>>());

    /// <summary>An edit of the record <paramref name="id"/> over <paramref name="version"/> by an editor of the levels 1 to <paramref name="level"/>.</summary>
    private RecordResult Edit(int level, string id, RecordDraft draft, int version = 1) =>
        RecordActions.Edit(_kiler.Reader(level), _records, id, version, draft, _time);

    /// <summary>The file of the record <paramref name="id"/> at its first version.</summary>
    private RecordFile Version1(string id)
    {
        var file = _records.Read(null).Records[id].File;
        Assert.Equal(1, file.Version);
        return Assert.IsType<RecordFile>(file.Model);
    }

    /// <summary>The record <paramref name="id"/> as a reader of <paramref name="level"/> reads it.</summary>
    private RecordView Read(int level, string id) => _records.Read(_kiler.Reader(level)).Records[id];

    private void Follow(LoadResult load)
    {
        _kiler.Team.Follow(load);
        _records.Follow(load);
    }
}

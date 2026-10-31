using System.Text.Json.Nodes;
using Kiler.Storage;

namespace Kiler.Core.Tests;

/// <summary>
/// The stashes and the records in memory (M5.3): each reader reads the groups of their keys and the padlocks of the rest, a
/// group that does not decrypt leaves the rest of the record, no <c>secret</c> value is kept, a lock drops what was
/// decrypted, and the changes of the share are followed.
/// </summary>
public class RecordsTests : IDisposable
{
    /// <summary>A backslash, so a test builds a JSON escape in code (SKILL.md, "Tests").</summary>
    private const string Slash = "\\";

    private readonly TestRecords _kiler = new();

    /// <summary>The level of the reader (0 for a guest) and the kind of each field of <see cref="TestRecords.Database"/> that they read.</summary>
    public static TheoryData<int, FieldKind[]> KindsByLevel => new()
    {
        // env, host, port, tns, user, password
        { 0, [FieldKind.Value, FieldKind.Locked, FieldKind.Locked, FieldKind.Empty, FieldKind.Locked, FieldKind.Locked] },
        { 1, [FieldKind.Value, FieldKind.Value, FieldKind.Value, FieldKind.Empty, FieldKind.Locked, FieldKind.Locked] },
        { 2, [FieldKind.Value, FieldKind.Value, FieldKind.Value, FieldKind.Empty, FieldKind.Locked, FieldKind.Locked] },
        { 3, [FieldKind.Value, FieldKind.Value, FieldKind.Value, FieldKind.Empty, FieldKind.Value, FieldKind.Secret] },
    };

    public void Dispose()
    {
        _kiler.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [MemberData(nameof(KindsByLevel))]
    public void EachReader_ReadsTheFieldsOfTheirLevels_AndThePadlocksOfTheRest(int level, FieldKind[] kinds)
    {
        _kiler.Write(_kiler.Database());
        using var records = _kiler.Open();

        var view = Read(records, level == 0 ? null : _kiler.Reader(level));

        Assert.Equal(["env", "host", "port", "tns", "user", "password"], view.Fields.Select(field => field.Key));
        Assert.Equal(kinds, view.Fields.Select(field => field.Kind));
        Assert.Equal([0, 1, 1, 1, 3, 3], view.Fields.Select(field => field.Level));
        Assert.Equal((FieldKind.Value, 0, "The main database"), (view.Description?.Kind, view.Description?.Level, view.Description?.Value?.Text));
        Assert.Equal("prod", view.Fields[0].Value?.Text);
        Assert.Equal(level >= 1 ? "db01.example.test" : null, view.Fields[1].Value?.Text);
        Assert.Equal(level >= 1 ? 1521d : null, view.Fields[2].Value?.Number);
        Assert.Equal(level >= 3 ? "pay_read" : null, view.Fields[4].Value?.Text);
        Assert.All(view.Fields.Where(field => field.Kind != FieldKind.Value), field => Assert.Null(field.Value));
    }

    [Fact]
    public void NoSecretValue_IsKept_WhereverTheSecretIs()
    {
        // Marked in a group as Kiler writes it; in public and unmarked in a group, which only a file changed by hand has.
        _kiler.Write(_kiler.Database());
        _kiler.Write(_kiler.Record("m4n5p6q7", publicFields: new Dictionary<string, FieldValue> { ["password"] = FieldValue.OfText(TestRecords.Password) }));
        _kiler.Write(_kiler.Record("r8s9t0v1", groups: [(3, $$"""{"password":"{{TestRecords.Password}}"}""")]));
        using var records = _kiler.Open();

        var set = records.Read(_kiler.Reader(3));

        Assert.Equal([3, 0, 3], set.Records.Values.Select(view => view.Fields.Single(field => field.Key == "password").Level));
        Assert.All(set.Records.Values, view =>
        {
            var password = Assert.Single(view.Fields, field => field.Key == "password");
            Assert.Equal(FieldKind.Secret, password.Kind);
            Assert.Null(password.Value);
            Assert.True(view.Fields.All(field => field.Value?.Text?.Contains(TestRecords.Password, StringComparison.Ordinal) != true), "A secret value is kept.");
        });
    }

    [Fact]
    public void ASecretMarkedInItsGroup_StaysASecret_WhenTheTemplateOnTheShareSaysText()
    {
        _kiler.Write(_kiler.Database());
        using var records = _kiler.Open();
        var session = _kiler.Reader(3);
        Follow(records, _kiler.Data.Copy.Load());

        // Anyone who can write the share makes the password a text in the template.
        var db = InitialTemplates.For(3)[0];
        var other = _kiler.Data.OpenCopy();
        other.Load();
        var forged = new TemplateFile("db", db.Name, db.Icon, [.. db.Fields.Select(field => field.Key == "password" ? new TemplateField("password", "Password", FieldType.Text, levelByEnv: field.LevelByEnv) : field)]);
        Assert.Equal(WriteOutcome.Written, other.Replace(db.Key, 1, forged.ToContent()).Outcome);
        Follow(records, _kiler.Data.Copy.Load());

        var view = Read(records, session);
        Assert.Equal(FieldType.Text, view.Template?.Fields[5].Type);
        Assert.Equal((FieldKind.Secret, null), (view.Fields[5].Kind, view.Fields[5].Value));
    }

    [Fact]
    public void AnEmptyField_HasTheLevelThatAValueWouldGet()
    {
        _kiler.Write(_kiler.Record(publicFields: new Dictionary<string, FieldValue> { ["env"] = FieldValue.OfText("dev") }));
        _kiler.Write(_kiler.Record("m4n5p6q7"));
        _kiler.Write(_kiler.Record("r8s9t0v1", publicFields: new Dictionary<string, FieldValue> { ["env"] = FieldValue.OfText("staging") }));
        using var records = _kiler.Open();

        var set = records.Read(null);

        // levelByEnv of dev; with no env, or one that levelByEnv does not name, the highest of levelByEnv.
        Assert.Equal([1, 3, 3], new[] { TestRecords.Id, "m4n5p6q7", "r8s9t0v1" }.Select(id => set.Records[id].Fields.Single(field => field.Key == "password").Level));
        Assert.All(set.Records.Values, view => Assert.Equal(FieldKind.Empty, view.Fields.Single(field => field.Key == "password").Kind));
        Assert.All(set.Records.Values, view => Assert.Equal((FieldKind.Empty, 0), (view.Description?.Kind, view.Description?.Level)));
    }

    [Theory]
    [InlineData(1, FieldKind.Locked)]
    [InlineData(2, FieldKind.Value)]
    public void ARaisedDescription_IsReadAtItsLevel(int level, FieldKind kind)
    {
        _kiler.Write(_kiler.Record(groups: [(2, """{"description":"Raised"}""")]));
        using var records = _kiler.Open();

        var description = Read(records, _kiler.Reader(level)).Description;

        Assert.Equal((kind, 2), (description?.Kind, description?.Level));
        Assert.Equal(level == 2 ? "Raised" : null, description?.Value?.Text);
    }

    [Theory]
    [InlineData("damaged")]
    [InlineData("another record")]
    [InlineData("another level")]
    [InlineData("not JSON")]
    [InlineData("an array")]
    [InlineData("a value of no shape")]
    [InlineData("a key twice")]
    [InlineData("bytes that are not UTF-8")]
    [InlineData("a name that does not decode")]
    [InlineData("a text that does not decode")]
    public void AGroupThatDoesNotDecrypt_OrDoesNotFit_LeavesTheRestOfTheRecord(string damage)
    {
        var group1 = """{"host":"db01.example.test","port":1521}""";
        var damaged = _kiler.Database().Protected[1];
        WriteDatabase(damage switch
        {
            "damaged" => damaged[..^4] + (damaged[^4] == 'A' ? "B" : "A") + damaged[^3..],
            "another record" => _kiler.Group("m4n5p6q7", 1, group1),
            "another level" => _kiler.Group(TestRecords.Id, 2, group1),
            "not JSON" => _kiler.Group(TestRecords.Id, 1, "host=db01"),
            "an array" => _kiler.Group(TestRecords.Id, 1, "[]"),
            "a value of no shape" => _kiler.Group(TestRecords.Id, 1, """{"host":"db01.example.test","port":[1521]}"""),
            "a key twice" => _kiler.Group(TestRecords.Id, 1, """{"host":"db01.example.test","host":"db02.example.test"}"""),
            // In a member that the template does not have, which the view checks without reading its text.
            "bytes that are not UTF-8" => _kiler.Group(TestRecords.Id, 1, [.. "{\"host\":\"db01.example.test\",\"port\":1521,\"x\":\""u8, 0xFF, .. "\"}"u8]),
            "a name that does not decode" => _kiler.Group(TestRecords.Id, 1, "{\"" + Slash + "udc00\":1,\"host\":\"db01.example.test\"}"),
            _ => _kiler.Group(TestRecords.Id, 1, "{\"host\":\"" + Slash + "ud800\",\"port\":1521}"),
        });
        using var records = _kiler.Open();

        var view = Read(records, _kiler.Reader(3));

        Assert.Equal(
            [FieldKind.Value, FieldKind.Unreadable, FieldKind.Unreadable, FieldKind.Empty, FieldKind.Value, FieldKind.Secret],
            view.Fields.Select(field => field.Kind));
        Assert.Equal(1, view.Fields[1].Level);
    }

    [Fact]
    public void ASignIn_OverAGroupThatDoesNotDecode_DoesNotThrow_AndLeavesTheRestOfTheRecord()
    {
        WriteDatabase(_kiler.Group(TestRecords.Id, 1, "{\"" + Slash + "udc00\":1}"));
        using var records = _kiler.Open();
        var session = _kiler.Reader(3);

        _kiler.Current.Start(session);

        Assert.Same(session, records.Set.Session);
        Assert.Equal(
            [FieldKind.Value, FieldKind.Unreadable, FieldKind.Unreadable, FieldKind.Empty, FieldKind.Value, FieldKind.Secret],
            records.Set.Records[TestRecords.Id].Fields.Select(field => field.Kind));
    }

    [Fact]
    public void AGroupOfALevelThatTheSessionHolds_IsTheTruth_AndTheListsLabelOnlyTheOthers()
    {
        var record = _kiler.Database();

        // The lists changed by hand: the host moved to 3, and the user named nowhere.
        var lists = new Dictionary<int, IReadOnlyList<string>> { [1] = ["port", "user"], [3] = ["host", "password"] };
        _kiler.Write(new RecordFile(record.Id, record.Template, record.Authorship, record.Stash, record.Name, record.Tags, record.Public, record.Protected, lists));
        using var records = _kiler.Open();

        var reader3 = Read(records, _kiler.Reader(3));
        var reader1 = Read(records, _kiler.Reader(1));

        Assert.Equal([FieldKind.Value, FieldKind.Value, FieldKind.Value, FieldKind.Empty, FieldKind.Value, FieldKind.Secret], reader3.Fields.Select(field => field.Kind));
        Assert.Equal([0, 1, 1, 1, 3, 3], reader3.Fields.Select(field => field.Level));
        Assert.Equal([FieldKind.Value, FieldKind.Locked, FieldKind.Value, FieldKind.Empty, FieldKind.Empty, FieldKind.Locked], reader1.Fields.Select(field => field.Kind));
        Assert.Equal((3, "db01.example.test"), (reader1.Fields[1].Level, reader3.Fields[1].Value?.Text));
    }

    [Fact]
    public void AKeyThatAGroupHas_TakesThePlaceOfPublic_AndOfALowerGroup()
    {
        // Lists that name each key once, as the model wants, over groups that a key holder wrote with a key twice.
        _kiler.Write(new RecordFile(
            TestRecords.Id,
            "db",
            TestRecords.Authors,
            [TestRecords.StashId],
            "Payments database",
            [],
            new Dictionary<string, FieldValue> { ["host"] = FieldValue.OfText("planted.example.test") },
            new Dictionary<int, string> { [1] = _kiler.Group(TestRecords.Id, 1, """{"port":1}"""), [2] = _kiler.Group(TestRecords.Id, 2, """{"host":"db01.example.test","port":2}""") },
            new Dictionary<int, IReadOnlyList<string>> { [1] = ["port"], [2] = [] }));
        using var records = _kiler.Open();

        var view = Read(records, _kiler.Reader(2));

        Assert.Equal(("db01.example.test", 2), (view.Fields[1].Value?.Text, view.Fields[1].Level));
        Assert.Equal((2d, 2), (view.Fields[2].Value?.Number, view.Fields[2].Level));
    }

    [Fact]
    public void ARecordWhoseTemplateIsMissing_KeepsOnlyItsHeader()
    {
        _kiler.Write(_kiler.Record(template: "gone", publicFields: new Dictionary<string, FieldValue> { ["env"] = FieldValue.OfText("prod") }, groups: [(1, """{"description":"Raised","host":"db01.example.test"}""")]));
        using var records = _kiler.Open();

        var view = Read(records, _kiler.Reader(3));

        Assert.Null(view.Template);
        Assert.Empty(view.Fields);
        Assert.Equal(("Raised", 1), (view.Description?.Value?.Text, view.Description?.Level));
        Assert.Equal("Payments database", view.File.Model?.Name);
    }

    [Fact]
    public void ARecordThatDoesNotFit_IsKept_WithItsReason()
    {
        Assert.Equal(WriteOutcome.Written, _kiler.Data.Copy.Create(RecordFile.KeyOf(TestRecords.Id), new JsonObject { ["template"] = "db" }).Outcome);
        using var records = _kiler.Open();

        var view = Read(records, _kiler.Reader(3));

        Assert.False(view.File.IsValid);
        Assert.Equal(1, view.File.Version);
        Assert.Equal("createdBy is missing or is not the name of an account.", view.File.Invalid);
        Assert.Null(view.Description);
        Assert.Empty(view.Fields);
    }

    [Fact]
    public void ASignIn_ReadsTheRecordsForTheSession_AndALock_DropsThem_BeforeAnyRequest()
    {
        _kiler.Write(_kiler.Database());
        _kiler.Write(_kiler.Database("m4n5p6q7"));
        Assert.Equal(WriteOutcome.Deleted, _kiler.Data.Copy.Delete(RecordFile.KeyOf("m4n5p6q7"), 1).Outcome);
        using var records = _kiler.Open();
        var session = _kiler.Reader(3);

        _kiler.Current.Start(session);
        var signedIn = records.Set;
        _kiler.Current.Lock();
        var locked = records.Set;

        Assert.Same(session, signedIn.Session);
        Assert.Equal(("pay_read", "pay_read"), (signedIn.Records[TestRecords.Id].Fields[4].Value?.Text, signedIn.Trash["m4n5p6q7"].Fields[4].Value?.Text));
        Assert.Null(locked.Session);
        Assert.Equal([FieldKind.Value, FieldKind.Locked, FieldKind.Locked, FieldKind.Empty, FieldKind.Locked, FieldKind.Locked], locked.Records[TestRecords.Id].Fields.Select(field => field.Kind));
        Assert.Equal(FieldKind.Locked, locked.Trash["m4n5p6q7"].Fields[4].Kind);
        Assert.Throws<SessionLockedException>(() => records.Read(session));
        Assert.Same(locked, records.Read(null));
    }

    [Fact]
    public void AnotherSession_ReadsTheRecordsForItself()
    {
        _kiler.Write(_kiler.Database());
        using var records = _kiler.Open();
        var session = _kiler.Reader(3);
        _kiler.Current.Start(session);

        var another = records.Read(_kiler.Reader(1));
        var again = records.Read(session);

        Assert.Equal(FieldKind.Locked, another.Records[TestRecords.Id].Fields[4].Kind);
        Assert.Same(session, again.Session);
        Assert.Equal(FieldKind.Value, again.Records[TestRecords.Id].Fields[4].Kind);
        Assert.Same(again, records.Read(session));
    }

    [Fact]
    public void ARecordAndAStash_NewChangedAndGoneOnTheShare_AreFollowed()
    {
        _kiler.Write(_kiler.Database());
        _kiler.Write(_kiler.Database("m4n5p6q7"));
        Assert.Equal(WriteOutcome.Written, _kiler.Data.Copy.Create(StashFile.KeyOf(TestRecords.StashId), new StashFile(TestRecords.StashId, "Payments", TestRecords.Authors).ToContent()).Outcome);
        using var records = _kiler.Open();
        var session = _kiler.Reader(3);
        Assert.Equal(2, records.Read(session).Records.Count);

        // Another Kiler changes the share: a new record and stash, a changed record and stash, and a record gone.
        var other = _kiler.Data.OpenCopy();
        other.Load();
        Assert.Equal(WriteOutcome.Written, other.Create(RecordFile.KeyOf("r8s9t0v1"), _kiler.Database("r8s9t0v1").ToContent()).Outcome);
        var changed = _kiler.Record(groups: [(1, """{"host":"db02.example.test"}""")]);
        Assert.Equal(WriteOutcome.Written, other.Replace(changed.Key, 1, changed.ToContent()).Outcome);
        Assert.Equal(WriteOutcome.Deleted, other.Delete(RecordFile.KeyOf("m4n5p6q7"), 1).Outcome);
        Assert.Equal(WriteOutcome.Written, other.Create(StashFile.KeyOf("e5f6g7h8"), new StashFile("e5f6g7h8", "Prod", TestRecords.Authors).ToContent()).Outcome);
        Assert.Equal(WriteOutcome.Written, other.Replace(StashFile.KeyOf(TestRecords.StashId), 1, new StashFile(TestRecords.StashId, "Payments and cards", TestRecords.Authors).ToContent()).Outcome);
        Follow(records, _kiler.Data.Copy.Load());

        var set = records.Read(session);
        Assert.Same(session, set.Session);
        Assert.Equal([TestRecords.Id, "r8s9t0v1"], set.Records.Keys);
        Assert.Equal((2, "db02.example.test", FieldKind.Empty), (set.Records[TestRecords.Id].File.Version, set.Records[TestRecords.Id].Fields[1].Value?.Text, set.Records[TestRecords.Id].Fields[4].Kind));
        Assert.Equal("pay_read", set.Records["r8s9t0v1"].Fields[4].Value?.Text);
        Assert.Equal(["Payments and cards", "Prod"], set.Stashes.Values.Select(stash => stash.Model?.Name));

        // The record gone is in the trash, read for the session (M5.10).
        Assert.Equal(["m4n5p6q7"], set.Trash.Keys);
        Assert.Equal("pay_read", set.Trash["m4n5p6q7"].Fields[4].Value?.Text);
    }

    [Fact]
    public void ATemplateChangedOnTheShare_ReadsItsRecordsAgain()
    {
        _kiler.Write(_kiler.Database());
        _kiler.Write(_kiler.Record("m4n5p6q7", template: "note", groups: [(1, """{"text":"A note"}""")]));
        _kiler.Write(_kiler.Database("r8s9t0v1"));
        Assert.Equal(WriteOutcome.Deleted, _kiler.Data.Copy.Delete(RecordFile.KeyOf("r8s9t0v1"), 1).Outcome);
        using var records = _kiler.Open();
        var session = _kiler.Reader(3);

        // The first load gives the documents again with the owners that the writes of the copy did not know (core.md).
        Follow(records, _kiler.Data.Copy.Load());
        var note = records.Read(session).Records["m4n5p6q7"];

        // The host becomes a secret, and the port leaves the template.
        var db = InitialTemplates.For(3)[0];
        var other = _kiler.Data.OpenCopy();
        other.Load();
        var changed = new TemplateFile("db", db.Name, db.Icon, [.. db.Fields.Where(field => field.Key is not "host" and not "port"), new("host", "Host", FieldType.Secret, level: 1)]);
        Assert.Equal(WriteOutcome.Written, other.Replace(db.Key, 1, changed.ToContent()).Outcome);
        var load = _kiler.Data.Copy.Load();
        Assert.False(load.TrashChanged, "The load of the template saw the trash change.");
        Follow(records, load);

        var set = records.Read(session);
        var view = set.Records[TestRecords.Id];
        Assert.Equal(["env", "tns", "user", "password", "host"], view.Fields.Select(field => field.Key));
        Assert.Equal((FieldKind.Secret, null), (view.Fields[4].Kind, view.Fields[4].Value));
        Assert.Same(note, set.Records["m4n5p6q7"]);
        Assert.Equal(["env", "tns", "user", "password", "host"], set.Trash["r8s9t0v1"].Fields.Select(field => field.Key));
    }

    [Fact]
    public void Reload_ReadsTheCopyAgain_ForTheSameSession()
    {
        using var records = _kiler.Open();
        var session = _kiler.Reader(3);
        Assert.Empty(records.Read(session).Records);
        _kiler.Write(_kiler.Database());
        _kiler.Write(_kiler.Database("m4n5p6q7"));
        Assert.Equal(WriteOutcome.Deleted, _kiler.Data.Copy.Delete(RecordFile.KeyOf("m4n5p6q7"), 1).Outcome);

        records.Reload();

        var set = records.Read(session);
        Assert.Same(session, set.Session);
        Assert.Equal("pay_read", set.Records[TestRecords.Id].Fields[4].Value?.Text);
        Assert.Equal("pay_read", set.Trash["m4n5p6q7"].Fields[4].Value?.Text);
    }

    [Fact]
    public void TheStashes_AreReadForEveryReader_TwoOfOneNameToo()
    {
        foreach (var (id, name) in new[] { (TestRecords.StashId, "Payments"), ("e5f6g7h8", "PAYMENTS") })
        {
            Assert.Equal(WriteOutcome.Written, _kiler.Data.Copy.Create(StashFile.KeyOf(id), new StashFile(id, name, TestRecords.Authors).ToContent()).Outcome);
        }

        Assert.Equal(WriteOutcome.Written, _kiler.Data.Copy.Create(StashFile.KeyOf("j9k0m1n2"), new JsonObject { ["name"] = "Broken" }).Outcome);
        using var records = _kiler.Open();

        var guest = records.Read(null).Stashes;

        Assert.Equal(["Payments", "PAYMENTS", null], guest.Values.Select(stash => stash.Model?.Name));
        Assert.Equal("createdBy is missing or is not the name of an account.", guest["j9k0m1n2"].Invalid);
        Assert.Equal(guest.Keys, records.Read(_kiler.Reader(3)).Stashes.Keys);
    }

    /// <summary>Writes <see cref="TestRecords.Database"/> with <paramref name="group1"/> in the place of its group of level 1, whose list stays.</summary>
    private void WriteDatabase(string group1)
    {
        var record = _kiler.Database();
        var groups = record.Protected.ToDictionary();
        groups[1] = group1;
        _kiler.Write(new RecordFile(record.Id, record.Template, record.Authorship, record.Stash, record.Name, record.Tags, record.Public, groups, record.Fields));
    }

    /// <summary>The team and the records follow <paramref name="load"/>, in the order of the host.</summary>
    private void Follow(Records records, LoadResult load)
    {
        _kiler.Team.Follow(load);
        records.Follow(load);
    }

    /// <summary>The record <see cref="TestRecords.Id"/> as <paramref name="session"/> reads it.</summary>
    private static RecordView Read(Records records, Session? session) => records.Read(session).Records[TestRecords.Id];
}

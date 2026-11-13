using System.Text;
using Kiler.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Core.Tests;

/// <summary>
/// The templates from the seal (M4.16b, the decision "A seal that fails" in <c>docs/tasks/M4.md</c>): a session takes the
/// newest version of each template that the admin key sealed, on the share or in the local copy, for the records, the view of
/// a record and the record actions; a template without one has its records read, but takes no new record or edit; a guest
/// takes the files on the share as they are.
/// </summary>
public sealed class SealedTemplatesTests : IDisposable
{
    private const string SealedName = "Server / database";

    private const string ForgedName = "Forged / database";

    private static readonly DocumentKey Db = TemplateFile.KeyOf("db");

    private readonly TestRecords _kiler = new();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));

    public SealedTemplatesTests()
    {
        _kiler.WriteStash(TestRecords.StashId, "Payments");
        _kiler.Write(_kiler.Database());
    }

    /// <summary>How someone who can write the share changes the template <c>db</c> outside Kiler, as <see cref="SealedFilesTests.Change"/> does <c>levels.json</c>.</summary>
    public enum Change
    {
        /// <summary>Its file written in place, at its version, with a name of its own: the seal no longer verifies.</summary>
        EditedInPlace,

        /// <summary>The older sealed version 1 copied from <c>history\templates\db\</c> over it.</summary>
        OlderSealedVersion,

        /// <summary>A version 3 written through a copy, as Kiler writes it, with the host and the TNS at level 0, without the seal.</summary>
        NewerNotSealed,

        /// <summary>Its file damaged.</summary>
        Damaged,

        /// <summary>Its file deleted outside the trash.</summary>
        Gone,
    }

    public void Dispose() => _kiler.Dispose();

    /// <summary>
    /// Whatever the change, a session takes version 2, which an admin sealed and the copy keeps, and so does the team read
    /// again, and a new team over the copy (a restart) once the copy has reported the change (a file gone is reported some 30 s
    /// later, the limit of <c>Team.Read</c>): the view of the record has its levels, and a new record and an edit put the host
    /// at its level 2, never at the level 0 of a forged version (the M5.3 finding on <c>DefaultLevel</c>).
    /// </summary>
    [Theory]
    [InlineData(Change.EditedInPlace)]
    [InlineData(Change.OlderSealedVersion)]
    [InlineData(Change.NewerNotSealed)]
    [InlineData(Change.Damaged)]
    [InlineData(Change.Gone)]
    public void TheRecordsAndTheirActions_OfATemplateChangedOnTheShare_TakeTheSealedVersionThatTheCopyKeeps(Change change)
    {
        using var records = SealedVersionTwo();
        var session = _kiler.Reader(3);
        Assert.Equal(2, TnsLevel(records, session));

        Make(change);
        Follow(records, _kiler.Data.Copy.Load());

        TeamFiles[] read = change == Change.Gone ? [_kiler.Team.Files] : [_kiler.Team.Files, new Team(_kiler.Data.Copy).Files];
        _kiler.Team.Reload();
        records.Reload();
        foreach (var files in (TeamFiles[])[.. read, _kiler.Team.Files])
        {
            var db = files.TemplatesFor(session)["db"];
            Assert.Equal((2, SealedName), (db.Version, db.Model?.Name));
            Assert.True(db.Model!.IsSealedFor(session), "The sealed version is not sealed for the session.");
        }

        Assert.Equal(SealedName, records.Read(session).Records[TestRecords.Id].Template?.Name);
        Assert.Equal(2, TnsLevel(records, session));

        var made = RecordActions.NewRecord(session, records, _kiler.Team.Files, NewDb(), _time);
        Assert.Equal(RecordOutcome.Done, made.Outcome);
        Assert.Equal(2, HostOf(made).Level);

        var edited = RecordActions.Edit(session, records, TestRecords.Id, 1, Edit(), _time);
        Assert.Equal(RecordOutcome.Done, edited.Outcome);
        Assert.Equal(2, HostOf(edited).Level);
    }

    /// <summary>
    /// A Kiler whose copy never saw a sealed version, as a new machine: a template that does not verify gives no level to a
    /// new value, so a new record and an edit are refused, and its records are read with it; one that cannot be read leaves
    /// nothing to write with, as before; an older sealed one is taken, as limit (1) of M3.md takes an older sealed user file.
    /// Another template that is sealed still takes a new record.
    /// </summary>
    [Theory]
    [InlineData(Change.EditedInPlace, RecordOutcome.TemplateNotSealed, RecordOutcome.TemplateNotSealed)]
    [InlineData(Change.NewerNotSealed, RecordOutcome.TemplateNotSealed, RecordOutcome.TemplateNotSealed)]
    [InlineData(Change.Damaged, RecordOutcome.Invalid, RecordOutcome.NoTemplate)]
    [InlineData(Change.Gone, RecordOutcome.Invalid, RecordOutcome.NoTemplate)]
    [InlineData(Change.OlderSealedVersion, RecordOutcome.Done, RecordOutcome.Done)]
    public void ATemplateWithoutASealedVersion_RefusesANewRecordAndAnEdit_AndItsRecordsAreRead(Change change, RecordOutcome newRecord, RecordOutcome edit)
    {
        SealedVersionTwo().Dispose();
        Make(change);
        var fresh = _kiler.Data.OpenCopy();
        fresh.Load();
        var team = new Team(fresh);
        using var records = new Records(fresh, team, _kiler.Current);
        var files = team.Files;
        var session = _kiler.Reader(3);

        Assert.Equal(newRecord, RecordActions.NewRecord(session, records, files, NewDb(), _time).Outcome);
        Assert.Equal(edit, RecordActions.Edit(session, records, TestRecords.Id, 1, Edit(), _time).Outcome);

        // The record is read, with the template that the session takes: a forged one gives the levels of its empty fields;
        // without a template that fits, its header and its description (docs/api.md, "Record").
        var view = records.Read(session).Records[TestRecords.Id];
        Assert.Equal("The main database", view.Description?.Value?.Text);
        Assert.Equal(view.Template is null ? null : "db01.example.test", view.Fields.SingleOrDefault(field => field.Key == "host")?.Value?.Text);
        var expected = change switch
        {
            Change.EditedInPlace => (ForgedName, 2),
            Change.NewerNotSealed => (ForgedName, 0),
            Change.OlderSealedVersion => (SealedName, 1),
            _ => ((string?)null, (int?)null),
        };
        Assert.Equal(expected, (view.Template?.Name, view.Template is null ? null : TnsLevel(records, session)));
        Assert.Equal(newRecord is RecordOutcome.Done, files.TemplatesFor(session).GetValueOrDefault("db")?.Model?.IsSealedFor(session) == true);

        var note = new RecordDraft("note", [TestRecords.StashId], "A note", null, [], new Dictionary<string, FieldValue?> { ["text"] = FieldValue.OfText("Text") }, new Dictionary<string, int>());
        Assert.Equal(RecordOutcome.Done, RecordActions.NewRecord(session, records, files, note, _time).Outcome);
    }

    /// <summary>
    /// A new team over a copy, as at a start, finds a template gone from the share once the copy reports it gone outside
    /// Kiler, some 30 s after it went (<c>Team.Read</c>, <see cref="DataCopy.ToRestore"/>), and takes its sealed version.
    /// </summary>
    [Fact]
    public void ANewTeam_TakesTheSealedVersionOfATemplateGoneFromTheShare_OnceTheCopyReportsIt()
    {
        SealedVersionTwo().Dispose();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var copy = _kiler.Data.OpenCopy(time);
        copy.Load();
        var session = _kiler.Reader(3);
        Make(Change.Gone);
        copy.Load();

        // The limit: before the copy reports it, nothing names the template.
        Assert.False(new Team(copy).Files.TemplatesFor(session).ContainsKey("db"), "A new team found the template gone before the copy reported it.");

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Contains(copy.Load().Outside, change => change.Key == Db);

        var db = new Team(copy).Files.TemplatesFor(session)["db"];
        Assert.Equal((2, SealedName), (db.Version, db.Model?.Name));
    }

    /// <summary>
    /// A guest has no key to check a seal with, so it takes the templates on the share as they are (decided by the user in
    /// M4.16b), as it takes <c>levels.json</c> (M4.16a): it reads level 0, which is in clear on the share, and writes nothing.
    /// </summary>
    [Fact]
    public void AGuest_TakesTheTemplatesOnTheShare_AsTheyAre()
    {
        using var records = SealedVersionTwo();
        Make(Change.NewerNotSealed);
        Follow(records, _kiler.Data.Copy.Load());

        Assert.Same(_kiler.Team.Files.Templates, _kiler.Team.Files.TemplatesFor(null));
        var db = _kiler.Team.Files.TemplatesFor(null)["db"];
        Assert.Equal((3, ForgedName), (db.Version, db.Model?.Name));
        Assert.False(db.Model!.IsSealedFor(null), "A template is sealed for a guest.");
        Assert.Equal(ForgedName, records.Read(null).Records[TestRecords.Id].Template?.Name);
        Assert.Equal(SealedName, records.Read(_kiler.Reader(3)).Records[TestRecords.Id].Template?.Name);
    }

    /// <summary>
    /// The templates of a session are those of the share while each is sealed there, and a change of the team that leaves
    /// them as they were keeps every view of a record, as <see cref="Records"/> reads again only the records whose template
    /// changed (by reference).
    /// </summary>
    [Fact]
    public void AChangeOfTheTeamThatLeavesTheTemplates_KeepsTheViewsOfTheRecords()
    {
        using var records = SealedVersionTwo();
        var session = _kiler.Reader(3);
        Assert.Same(_kiler.Team.Files.Templates, _kiler.Team.Files.TemplatesFor(session));
        var before = records.Read(session).Records[TestRecords.Id];

        var other = _kiler.Data.OpenCopy();
        other.Load();
        Assert.Equal(WriteOutcome.Written, other.Create(SettingsFile.Key, new SettingsFile(guestMode: false, autoLockMinutes: 30).ToContent()).Outcome);
        var files = _kiler.Team.Files;
        Follow(records, _kiler.Data.Copy.Load());

        Assert.NotSame(files, _kiler.Team.Files);
        Assert.Same(before, records.Read(session).Records[TestRecords.Id]);

        // A template that the session takes from the copy: a new TeamFiles keeps it, so the view stays too.
        Make(Change.NewerNotSealed);
        Follow(records, _kiler.Data.Copy.Load());
        var taken = records.Read(session).Records[TestRecords.Id];
        Assert.Equal(SealedName, taken.Template?.Name);
        Assert.Equal(WriteOutcome.Written, other.Replace(SettingsFile.Key, 1, new SettingsFile(guestMode: false, autoLockMinutes: 20).ToContent()).Outcome);
        Follow(records, _kiler.Data.Copy.Load());

        Assert.Same(taken, records.Read(session).Records[TestRecords.Id]);
    }

    /// <summary>
    /// The template <c>db</c> as the admin changed it in version 2, sealed, with the host and the TNS raised to level 2, and
    /// the records over the copy, which has followed the load that gives the documents again with their owners (core.md).
    /// </summary>
    private Records SealedVersionTwo()
    {
        Assert.Equal(WriteOutcome.Written, _kiler.Data.Copy.Replace(Db, 1, Template(SealedName, level: 2).Sealed(_kiler.AdminKey, 2).ToContent()).Outcome);
        _kiler.Team.Reload();
        var records = _kiler.Open();
        Follow(records, _kiler.Data.Copy.Load());
        return records;
    }

    /// <summary>The template <c>db</c> of a Kiler of three levels, named <paramref name="name"/>, with the host and the TNS at <paramref name="level"/>, not sealed.</summary>
    private static TemplateFile Template(string name, int level)
    {
        var db = InitialTemplates.For(3)[0];
        return new("db", name, db.Icon, [.. db.Fields.Select(field => field.Key switch
        {
            "host" => new TemplateField("host", "Host", FieldType.Text, level: level, list: true),
            "tns" => new TemplateField("tns", "TNS", FieldType.Code, level: level, language: "text"),
            _ => field,
        })]);
    }

    /// <summary>Changes the template <c>db</c> of the share at version 2 outside the copy of the team.</summary>
    private void Make(Change change)
    {
        var path = Path.Combine(_kiler.Data.Folder, "templates", "db.json");
        switch (change)
        {
            case Change.EditedInPlace:
                var text = File.ReadAllText(path, Encoding.UTF8);
                Assert.Contains(SealedName, text, StringComparison.Ordinal);
                File.WriteAllText(path, text.Replace(SealedName, ForgedName, StringComparison.Ordinal), new UTF8Encoding(false));
                File.SetLastWriteTimeUtc(path, File.GetCreationTimeUtc(path).AddMinutes(1));
                break;
            case Change.OlderSealedVersion:
                var older = File.ReadAllBytes(Path.Combine(_kiler.Data.Folder, "history", "templates", "db", "v1.json"));
                File.Delete(path);
                File.WriteAllBytes(path, older);
                File.SetCreationTimeUtc(path, File.GetLastWriteTimeUtc(path));
                break;
            case Change.NewerNotSealed:
                var other = _kiler.Data.OpenCopy();
                other.Load();
                Assert.Equal(WriteOutcome.Written, other.Replace(Db, 2, Template(ForgedName, level: 0).ToContent()).Outcome);
                break;
            case Change.Damaged:
                File.WriteAllText(path, "{", Encoding.UTF8);
                break;
            case Change.Gone:
                File.Delete(path);
                break;
        }
    }

    /// <summary>The team and the records follow <paramref name="load"/>, in the order of the host.</summary>
    private void Follow(Records records, LoadResult load)
    {
        _kiler.Team.Follow(load);
        records.Follow(load);
    }

    /// <summary>The level of the TNS of the record <see cref="TestRecords.Id"/>, which has none, so its level is that of its template.</summary>
    private static int TnsLevel(Records records, Session session) =>
        records.Read(session).Records[TestRecords.Id].Fields.Single(field => field.Key == "tns").Level;

    private static RecordField HostOf(RecordResult result) => result.Record!.Fields.Single(field => field.Key == "host");

    /// <summary>A new database of <c>dev</c> with a host.</summary>
    private static RecordDraft NewDb() => new(
        "db",
        [TestRecords.StashId],
        "Billing database",
        null,
        [],
        new Dictionary<string, FieldValue?> { ["env"] = FieldValue.OfText("dev"), ["host"] = FieldValue.OfText("db02.example.test") },
        new Dictionary<string, int>());

    /// <summary>The edit of the record <see cref="TestRecords.Id"/> as it is, at every level, the password left out, so kept.</summary>
    private static RecordDraft Edit() => new(
        null,
        [TestRecords.StashId],
        "Payments database",
        "The main database",
        ["payments"],
        new Dictionary<string, FieldValue?>
        {
            ["env"] = FieldValue.OfText("prod"),
            ["host"] = FieldValue.OfText("db01.example.test"),
            ["port"] = FieldValue.OfNumber(1521),
            ["user"] = FieldValue.OfText("pay_read"),
        },
        new Dictionary<string, int>());
}

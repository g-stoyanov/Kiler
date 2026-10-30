using System.Text;
using System.Text.Json.Nodes;
using Kiler.Crypto;
using Kiler.Storage;

namespace Kiler.Core.Tests;

/// <summary>
/// A data folder with the initial templates of a Kiler of three levels, and a key of each level (M5.3): it makes the files of
/// records with their groups encrypted as Kiler does, sessions over its keys, and the records in memory over its copy. The
/// sessions share its keys, so a lock of one disposes them for all: a test locks only at its end.
/// </summary>
internal sealed class TestRecords : IDisposable
{
    public const string Id = "k3x9q2mf";

    public const string StashId = "a1b2c3d4";

    /// <summary>A made-up password, compared only with <c>Assert.True</c> (SKILL.md, "Tests").</summary>
    public const string Password = "made-up-password-7";

    private readonly Dictionary<int, SecretKey> _keys = Enumerable.Range(1, 3).ToDictionary(level => level, _ => SecretKey.New());

    public TestRecords()
    {
        foreach (var template in InitialTemplates.For(3))
        {
            Data.WriteAndRead(template.Key, template.ToContent());
        }

        Team = new Team(Data.Copy);
    }

    public static Authorship Authors { get; } = new(
        "demo.admin",
        new DateTime(2026, 9, 30, 8, 10, 0, DateTimeKind.Utc),
        "demo.editor",
        new DateTime(2026, 10, 21, 11, 32, 0, DateTimeKind.Utc));

    public TestData Data { get; } = new();

    public Team Team { get; }

    public CurrentSession Current { get; } = new();

    /// <summary>A session that holds the keys of the levels 1 to <paramref name="level"/>.</summary>
    public Session Reader(int level) => new(
        new UserFile("demo.user", "Demo User", level, canEdit: false, isAdmin: false, mustChangePassword: false, new LockedKeys("salt", new Dictionary<int, string>())),
        userVersion: 1,
        _keys.Where(key => key.Key <= level).ToDictionary(),
        _keys.Where(key => key.Key <= level).ToDictionary(key => key.Key, key => $"made-up check {key.Key}"));

    /// <summary>The group of <paramref name="json"/> encrypted with the key of <paramref name="level"/> for the record <paramref name="id"/>.</summary>
    public string Group(string id, int level, string json) => Group(id, level, Encoding.UTF8.GetBytes(json));

    /// <summary>The group of <paramref name="utf8"/> as it is, for bytes that are not UTF-8.</summary>
    public string Group(string id, int level, byte[] utf8) => FieldGroup.Encrypt(_keys[level], utf8, id, level);

    /// <summary>The JSON of the group of <paramref name="level"/> of <paramref name="record"/>, decrypted (M5.4); it can hold a secret, so a test compares it with <c>Assert.True</c>.</summary>
    public string Open(RecordFile record, int level) => Encoding.UTF8.GetString(FieldGroup.Decrypt(_keys[level], record.Protected[level], record.Id, level));

    /// <summary>Writes a stash through the copy.</summary>
    public void WriteStash(string id, string name) =>
        Assert.Equal(WriteOutcome.Written, Data.Copy.Create(StashFile.KeyOf(id), new StashFile(id, name, Authors).ToContent()).Outcome);

    /// <summary>
    /// A record of <paramref name="template"/> whose <c>public</c> has <paramref name="publicFields"/>, and a group of each of
    /// <paramref name="groups"/>, its JSON encrypted with the key of its level and its keys listed in <c>fields</c>.
    /// </summary>
    public RecordFile Record(string id = Id, string template = "db", IReadOnlyDictionary<string, FieldValue>? publicFields = null, params (int Level, string Json)[] groups) =>
        new(
            id,
            template,
            Authors,
            [StashId],
            "Payments database",
            ["payments"],
            publicFields ?? new Dictionary<string, FieldValue>(),
            groups.ToDictionary(group => group.Level, group => Group(id, group.Level, group.Json)),
            groups.ToDictionary(group => group.Level, group => (IReadOnlyList<string>)[.. JsonNode.Parse(group.Json)!.AsObject().Select(member => member.Key)]));

    /// <summary>
    /// The database of design 5.2: <c>prod</c>, the host and the port at 1, and the user and <see cref="Password"/> at 3, the
    /// password marked as a secret in its group, as Kiler writes it.
    /// </summary>
    public RecordFile Database(string id = Id) => Record(
        id,
        publicFields: new Dictionary<string, FieldValue> { ["env"] = FieldValue.OfText("prod"), ["description"] = FieldValue.OfText("The main database") },
        groups: [(1, """{"host":"db01.example.test","port":1521}"""), (3, $$$"""{"user":"pay_read","password":{"secret":"{{{Password}}}"}}""")]);

    /// <summary>Writes <paramref name="record"/> through the copy.</summary>
    public void Write(RecordFile record) => Assert.Equal(WriteOutcome.Written, Data.Copy.Create(record.Key, record.ToContent()).Outcome);

    /// <summary>The records in memory over the copy, the team and <see cref="Current"/>.</summary>
    public Records Open() => new(Data.Copy, Team, Current);

    public void Dispose()
    {
        Current.Dispose();
        Data.Dispose();
        foreach (var key in _keys.Values)
        {
            key.Dispose();
        }
    }
}

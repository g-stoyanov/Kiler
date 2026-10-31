using System.Text;
using Kiler.Crypto;
using Kiler.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Core.Tests;

/// <summary>
/// The audit of the secrets of the top level (M6.18, <see cref="Audit"/>): a view of the top level is one line of the file of
/// its account and day, appended after the value is decrypted and before it is given; a view that cannot be recorded gives no
/// value; and a read of a span gives its entries, the newest first, with the lines that do not fit counted.
/// </summary>
public class AuditTests : IDisposable
{
    private const string Account = "demo.user";

    private readonly TestRecords _kiler = new();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 10, 0, 0, TimeSpan.Zero));

    /// <summary>A <c>levels.json</c> of the three levels of <see cref="TestRecords"/>: Prod, level 3, is the top level.</summary>
    private static LevelsFile Levels { get; } = new(
        [new(0, "Guest", null), new(1, "Team", "check-1"), new(2, "Test", "check-2"), new(3, "Prod", "check-3")],
        new LockedKeys("salt", new Dictionary<int, string> { [1] = "locked-1", [2] = "locked-2", [3] = "locked-3" }));

    private string AuditFolder => Path.Combine(_kiler.Data.Folder, "audit");

    public void Dispose()
    {
        _kiler.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ShowAndCopy_OfASecretOfTheTopLevel_GiveTheValue_AndOneLineEach()
    {
        _kiler.Write(_kiler.Database());
        using var records = _kiler.Open();
        var session = _kiler.Reader(3);

        var shown = Secret(records, session, SecretUse.Show, Levels, out var value);
        _time.Advance(TimeSpan.FromSeconds(1.5));
        var copied = Secret(records, session, SecretUse.Copy, Levels, out var copy);

        Assert.Equal((SecretOutcome.Value, SecretOutcome.Value), (shown, copied));
        Assert.True(value == TestRecords.Password && copy == TestRecords.Password, "The value is not the one of the secret.");
        Assert.Equal(
            """{"at":"2026-10-31T10:00:00Z","account":"demo.user","record":"k3x9q2mf","name":"Payments database","field":"password","use":"show","level":3}""" + "\n"
            + """{"at":"2026-10-31T10:00:01.5Z","account":"demo.user","record":"k3x9q2mf","name":"Payments database","field":"password","use":"copy","level":3}""" + "\n",
            File.ReadAllText(Path.Combine(AuditFolder, "2026-10-31", Account + ".jsonl"), Encoding.UTF8));
    }

    [Theory]
    [InlineData(2, 3, "password", SecretOutcome.Value)]
    [InlineData(3, 2, "password", SecretOutcome.Locked)]
    [InlineData(3, 3, "user", SecretOutcome.NotSecret)]
    public void ASecretOfALowerLevel_OneAboveTheSession_OrNoSecret_WritesNothing(int group, int level, string key, SecretOutcome expected)
    {
        // The database has its password at level 3, the top level; the other record at the level of the group.
        _kiler.Write(group == 3
            ? _kiler.Database()
            : _kiler.Record(groups: [(group, $$$"""{"password":{"secret":"{{{TestRecords.Password}}}"}}""")]));
        using var records = _kiler.Open();
        var session = _kiler.Reader(level);

        var outcome = Audit.Secret(Read(records, session), key, SecretUse.Show, session, Levels, _kiler.Data.Copy, _time, out var value);

        Assert.Equal(expected, outcome);
        Assert.True(expected == SecretOutcome.Value ? value == TestRecords.Password : value is null, "The value is not the one that the outcome gives.");
        Assert.Empty(Directory.GetFileSystemEntries(AuditFolder));
    }

    [Theory]
    [InlineData(1, 3, false)]
    [InlineData(2, 3, false)]
    [InlineData(3, 3, true)]
    [InlineData(3, 2, true)]
    [InlineData(1, null, true)]
    [InlineData(0, null, false)]
    public void TheAuditedLevels_AreTheTopLevelAndAbove_OrEveryProtectedLevelWithoutLevels(int level, int? top, bool audited)
    {
        // A levels.json changed since the sign-in can have a lower top level than the keys of the session.
        LevelsFile? levels = top is { } count
            ? new([.. Levels.Levels.Take(count + 1)], new LockedKeys("salt", Levels.Recovery.Keys.Where(key => key.Key <= count).ToDictionary()))
            : null;

        Assert.Equal(audited, Audit.IsAudited(level, levels));
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("no audit folder")]
    public void AViewThatCannotBeRecorded_GivesNoValue_AndWritesNothing(string why)
    {
        _kiler.Write(_kiler.Database());
        using var records = _kiler.Open();
        var session = _kiler.Reader(3);
        var view = Read(records, session);
        var away = $"{_kiler.Data.Folder}-away";
        if (why == "offline")
        {
            Directory.Move(_kiler.Data.Folder, away);
            Assert.NotNull(Xunit.Record.Exception(() => _kiler.Data.Copy.Load()));
            Assert.True(_kiler.Data.Copy.Offline, "The copy is not offline.");
        }
        else
        {
            Directory.Delete(AuditFolder);
        }

        var outcome = Audit.Secret(view, "password", SecretUse.Copy, session, Levels, _kiler.Data.Copy, _time, out var value);

        Assert.Equal(SecretOutcome.NotRecorded, outcome);
        Assert.True(value is null, "A value is given.");
        if (why == "offline")
        {
            Directory.Move(away, _kiler.Data.Folder);
            Assert.Empty(Directory.GetFileSystemEntries(AuditFolder));
        }
        else
        {
            Assert.False(Directory.Exists(AuditFolder), "The audit folder was made.");
        }
    }

    [Fact]
    public void ALongName_IsCutToFitTheLine_NeverInTheMiddleOfAPair()
    {
        // A Cyrillic letter is two bytes, and a pair twelve, as the encoder escapes a character outside the BMP: the line can
        // end at most eleven bytes short of the cap.
        var name = string.Concat(Enumerable.Repeat("Я😀", 1100));
        var entry = new AuditEntry(_time.GetUtcNow().UtcDateTime, Account, TestRecords.Id, name, "password", SecretUse.Show, 3);

        var line = Audit.LineOf(entry);

        Assert.InRange(line.Length, DataFolder.MaxAuditLine - 11, DataFolder.MaxAuditLine);
        var read = Assert.IsType<AuditEntry>(Audit.TryRead(line, new DateOnly(2026, 10, 31), Account));
        Assert.StartsWith(read.Name, name, StringComparison.Ordinal);
        Assert.False(char.IsHighSurrogate(read.Name[^1]), "The name ends in half a pair.");
        Assert.Equal(entry with { Name = read.Name }, read);
    }

    [Fact]
    public void ARead_GivesTheEntriesOfItsSpan_NewestFirst_OverTwoDays_AndCountsTheLinesThatDoNotFit()
    {
        _kiler.Write(_kiler.Database());
        using var records = _kiler.Open();
        var session = _kiler.Reader(3);
        var times = new List<DateTime>();
        foreach (var step in new[] { TimeSpan.Zero, TimeSpan.FromHours(13), TimeSpan.FromHours(1), TimeSpan.FromHours(1) })
        {
            // 10:00 and 23:00 of the 31st, then 00:00 and 01:00 of the 1st.
            _time.Advance(step);
            times.Add(_time.GetUtcNow().UtcDateTime);
            Assert.Equal(SecretOutcome.Value, Secret(records, session, SecretUse.Show, Levels, out _));
        }

        File.AppendAllText(Path.Combine(AuditFolder, "2026-10-31", Account + ".jsonl"), "not a line of the audit\n");

        // From the first view, which counts, to the last, which does not.
        var span = Audit.Read(_kiler.Data.Copy, times[0], times[3]);

        Assert.Equal([times[2], times[1], times[0]], span.Entries.Select(entry => entry.At));
        Assert.Equal(1, span.Skipped);
        Assert.Equal(new AuditEntry(times[2], Account, TestRecords.Id, "Payments database", "password", SecretUse.Show, 3), span.Entries[0]);
    }

    [Theory]
    [InlineData("""{"at":"2026-10-31T10:00:00Z","account":"demo.user","record":"k3x9q2mf","name":"","field":"password","use":"copy","level":9}""", true)]
    [InlineData("""{"at":"2026-10-31T10:00:00Z","account":"demo.user","record":"k3x9q2mf","name":"N","field":"password","use":"copy","level":3,"more":1}""", true)]
    [InlineData("""{"at":"2026-10-30T10:00:00Z","account":"demo.user","record":"k3x9q2mf","name":"N","field":"password","use":"copy","level":3}""", false)]
    [InlineData("""{"at":"2026-10-31T10:00:00+01:00","account":"demo.user","record":"k3x9q2mf","name":"N","field":"password","use":"copy","level":3}""", false)]
    [InlineData("""{"at":"2026-10-31T10:00:00Z","account":"demo.admin","record":"k3x9q2mf","name":"N","field":"password","use":"copy","level":3}""", false)]
    [InlineData("""{"at":"2026-10-31T10:00:00Z","account":"demo.user","record":"../x","name":"N","field":"password","use":"copy","level":3}""", false)]
    [InlineData("""{"at":"2026-10-31T10:00:00Z","account":"demo.user","record":"k3x9q2mf","field":"password","use":"copy","level":3}""", false)]
    [InlineData("""{"at":"2026-10-31T10:00:00Z","account":"demo.user","record":"k3x9q2mf","name":"N","field":"","use":"copy","level":3}""", false)]
    [InlineData("""{"at":"2026-10-31T10:00:00Z","account":"demo.user","record":"k3x9q2mf","name":"N","field":"password","use":"print","level":3}""", false)]
    [InlineData("""{"at":"2026-10-31T10:00:00Z","account":"demo.user","record":"k3x9q2mf","name":"N","field":"password","use":"copy","level":0}""", false)]
    [InlineData("""{"at":"2026-10-31T10:00:00Z","account":"demo.user","record":"k3x9q2mf","name":"N","field":"password","use":"copy","level":"3"}""", false)]
    [InlineData("""null""", false)]
    [InlineData("""[]""", false)]
    public void ALine_FitsOnlyAsKilerWritesIt_InTheFileOfItsAccountAndDay(string line, bool fits)
    {
        Assert.Equal(fits, Audit.TryRead(Encoding.UTF8.GetBytes(line), new DateOnly(2026, 10, 31), Account) is not null);
    }

    [Theory]
    [InlineData("a name", true)]
    [InlineData("an escape that does not decode", false)]
    [InlineData("a byte that is not UTF-8", false)]
    public void ALineThatIsNotUtf8_OrWhoseEscapeDoesNotDecode_DoesNotFit(string name, bool fits)
    {
        // The escape is built in code (SKILL.md, "Tests").
        const string Before = "{\"at\":\"2026-10-31T10:00:00Z\",\"account\":\"demo.user\",\"record\":\"k3x9q2mf\",\"name\":\"";
        const string After = "\",\"field\":\"password\",\"use\":\"copy\",\"level\":3}";
        byte[] line = name switch
        {
            "a name" => Encoding.UTF8.GetBytes(Before + "N" + After),
            "an escape that does not decode" => Encoding.UTF8.GetBytes(Before + "\\" + "ud800" + After),
            _ => [.. Encoding.UTF8.GetBytes(Before), 0xFF, .. Encoding.UTF8.GetBytes(After)],
        };

        Assert.Equal(fits, Audit.TryRead(line, new DateOnly(2026, 10, 31), Account) is not null);
    }

    [Theory]
    [InlineData("2026-10-31T00:00:00Z", "2026-10-31T00:00:00Z", "to is not after from.")]
    [InlineData("2026-10-31T00:00:00Z", "2026-10-30T00:00:00Z", "to is not after from.")]
    [InlineData("2026-10-01T00:00:00Z", "2026-11-01T00:00:00Z", null)]
    [InlineData("2026-10-01T00:00:00Z", "2026-11-01T00:00:00.0000001Z", "to is more than 31 days after from.")]
    [InlineData("9999-12-30T00:00:00Z", "9999-12-31T00:00:00Z", null)]
    [InlineData("9999-12-30T00:00:00Z", "9999-12-31T00:00:00.0000001Z", "to is after the last date that Kiler reads.")]
    public void ASpan_IsAfterItsStart_AtMost31Days_AndEndsWhereTheReadCan(string from, string to, string? reason)
    {
        Assert.True(Audit.TryReadTime(from, out var start), "from does not read.");
        Assert.True(Audit.TryReadTime(to, out var end), "to does not read.");

        Assert.Equal(reason, Audit.InvalidSpan(start, end));
        if (reason is null)
        {
            Assert.Empty(Audit.Read(_kiler.Data.Copy, start, end).Entries);
        }
    }

    private SecretOutcome Secret(Records records, Session session, SecretUse use, LevelsFile? levels, out string? value) =>
        Audit.Secret(Read(records, session), "password", use, session, levels, _kiler.Data.Copy, _time, out value);

    /// <summary>The record <see cref="TestRecords.Id"/> as <paramref name="session"/> reads it.</summary>
    private static RecordView Read(Records records, Session session) => records.Read(session).Records[TestRecords.Id];
}

using System.Globalization;

namespace Kiler.Core.Tests;

/// <summary>
/// The request of a secret (M5.7, <see cref="RecordView.Secret"/>): the group is decrypted again for a session that holds its
/// level, the value is the text of the secret wherever it is, and every other field gets its refusal.
/// </summary>
public class RecordSecretTests : IDisposable
{
    /// <summary>A backslash, so a test builds a JSON escape in code (SKILL.md, "Tests").</summary>
    private const string Slash = "\\";

    private readonly TestRecords _kiler = new();

    public void Dispose()
    {
        _kiler.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(1, SecretOutcome.Locked)]
    [InlineData(2, SecretOutcome.Locked)]
    [InlineData(3, SecretOutcome.Value)]
    public void TheSecret_IsGiven_OnlyToASessionThatHoldsItsLevel(int level, SecretOutcome expected)
    {
        _kiler.Write(_kiler.Database());
        using var records = _kiler.Open();
        var session = _kiler.Reader(level);

        var outcome = Read(records, session).Secret("password", session, out var value);

        Assert.Equal(expected, outcome);
        Assert.True(level == 3 ? value == TestRecords.Password : value is null, "The value is not the one that the level gives.");
    }

    [Theory]
    [InlineData("marked", "password", TestRecords.Password)]
    [InlineData("marked where the template says text", "user", TestRecords.Password)]
    [InlineData("a text where the template says secret", "password", TestRecords.Password)]
    [InlineData("a code where the template says secret", "password", TestRecords.Password)]
    [InlineData("a number where the template says secret", "password", "1521.5")]
    [InlineData("in public", "password", TestRecords.Password)]
    public void ASecret_GivesItsText_WhereverItIs(string where, string key, string text)
    {
        // Kiler writes only the first; the rest come from a template changed after the record, or a file changed by hand.
        _kiler.Write(where switch
        {
            "marked" => _kiler.Database(),
            "marked where the template says text" => _kiler.Record(groups: [(3, $$$"""{"user":{"secret":"{{{TestRecords.Password}}}"}}""")]),
            "a text where the template says secret" => _kiler.Record(groups: [(3, $$"""{"password":"{{TestRecords.Password}}"}""")]),
            "a code where the template says secret" => _kiler.Record(groups: [(3, $$$"""{"password":{"language":"text","code":"{{{TestRecords.Password}}}"}}""")]),
            "a number where the template says secret" => _kiler.Record(groups: [(3, """{"password":1521.5}""")]),
            _ => _kiler.Record(publicFields: new Dictionary<string, FieldValue> { ["password"] = FieldValue.OfText(TestRecords.Password) }),
        });
        using var records = _kiler.Open();
        var session = _kiler.Reader(3);

        // A culture with a decimal comma, so a number is its text as JSON writes it, not as the culture of the machine would.
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        SecretOutcome outcome;
        string? value;
        try
        {
            outcome = Read(records, session).Secret(key, session, out value);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        Assert.Equal(SecretOutcome.Value, outcome);
        Assert.True(value == text, "The value is not the text of the secret.");
    }

    [Theory]
    [InlineData("nope", SecretOutcome.NoField)]
    [InlineData("PASSWORD", SecretOutcome.NoField)]
    [InlineData("description", SecretOutcome.NoField)]
    [InlineData("", SecretOutcome.NoField)]
    [InlineData("host", SecretOutcome.NotSecret)]
    [InlineData("tns", SecretOutcome.NotSecret)]
    public void AFieldThatTheTemplateDoesNotHave_OrThatIsNoSecret_IsRefused(string key, SecretOutcome expected)
    {
        _kiler.Write(_kiler.Database());
        using var records = _kiler.Open();
        var session = _kiler.Reader(3);

        var outcome = Read(records, session).Secret(key, session, out var value);

        Assert.Equal(expected, outcome);
        Assert.Null(value);
    }

    [Theory]
    [InlineData("no password", "password", SecretOutcome.Empty)]
    [InlineData("an empty secret", "password", SecretOutcome.Empty)]
    [InlineData("a group of another record", "password", SecretOutcome.Unreadable)]
    [InlineData("a group of another record", "user", SecretOutcome.Unreadable)]
    [InlineData("a secret whose escape does not decode", "password", SecretOutcome.Unreadable)]
    [InlineData("a template that is missing", "password", SecretOutcome.NoField)]
    public void ASecretWithoutAValueThatCanBeRead_IsRefused(string record, string key, SecretOutcome expected)
    {
        _kiler.Write(record switch
        {
            "no password" => _kiler.Record(groups: [(3, """{"user":"pay_read"}""")]),
            "an empty secret" => _kiler.Record(groups: [(3, """{"password":{"secret":""}}""")]),
            "a group of another record" => WithGroup3(_kiler.Group("m4n5p6q7", 3, """{"user":"pay_read"}""")),

            // The view checks a secret without reading its text, so only the request meets the escape.
            "a secret whose escape does not decode" => WithGroup3(_kiler.Group(TestRecords.Id, 3, "{\"password\":{\"secret\":\"" + Slash + "ud800\"}}")),
            _ => _kiler.Record(template: "gone", groups: [(3, $$$"""{"password":{"secret":"{{{TestRecords.Password}}}"}}""")]),
        });
        using var records = _kiler.Open();
        var session = _kiler.Reader(3);
        var view = Read(records, session);

        var outcome = view.Secret(key, session, out var value);

        Assert.Equal(expected, outcome);
        Assert.Null(value);
    }

    [Fact]
    public void AViewOfAnotherSession_GivesNoSecretAboveTheLevelOfThisOne()
    {
        _kiler.Write(_kiler.Database());
        using var records = _kiler.Open();

        var outcome = Read(records, _kiler.Reader(3)).Secret("password", _kiler.Reader(1), out var value);

        Assert.Equal(SecretOutcome.Locked, outcome);
        Assert.Null(value);
    }

    [Theory]
    [InlineData("in a group")]
    [InlineData("in public")]
    public void ALockedSession_IsTheLockedAnswer(string where)
    {
        _kiler.Write(where == "in a group"
            ? _kiler.Database()
            : _kiler.Record(publicFields: new Dictionary<string, FieldValue> { ["password"] = FieldValue.OfText(TestRecords.Password) }));
        using var records = _kiler.Open();
        var session = _kiler.Reader(3);
        var view = Read(records, session);

        // The keys are shared by the sessions of the test, so it locks at its end.
        _kiler.Current.Start(session);
        _kiler.Current.Lock();

        Assert.Throws<SessionLockedException>(() => view.Secret("password", session, out _));
    }

    /// <summary><see cref="TestRecords.Database"/> with <paramref name="group3"/> in the place of its group of level 3, whose list stays.</summary>
    private RecordFile WithGroup3(string group3)
    {
        var record = _kiler.Database();
        var groups = record.Protected.ToDictionary();
        groups[3] = group3;
        return new RecordFile(record.Id, record.Template, record.Authorship, record.Stash, record.Name, record.Tags, record.Public, groups, record.Fields);
    }

    /// <summary>The record <see cref="TestRecords.Id"/> as <paramref name="session"/> reads it.</summary>
    private static RecordView Read(Records records, Session session) => records.Read(session).Records[TestRecords.Id];
}

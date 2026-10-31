using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Kiler.Storage;

namespace Kiler.Core;

/// <summary>What the request of a secret is for (design 8.2, docs/api.md): "show" or "copy".</summary>
public enum SecretUse
{
    Show,
    Copy,
}

/// <summary>
/// A view of a secret in the audit (M6.18, design 12): when, in UTC, who, the record with its name at that moment, the field,
/// show or copy, and the level of the field in the record.
/// </summary>
public sealed record AuditEntry(DateTime At, string Account, string Record, string Name, string Field, SecretUse Use, int Level);

/// <summary>
/// The entries of a span of the audit, the newest first, and <see cref="Skipped"/>: the lines that do not fit, and each file
/// or folder of the audit that was not read whole, once, as its number of lines is not known.
/// </summary>
public sealed record AuditSpan(IReadOnlyList<AuditEntry> Entries, int Skipped);

/// <summary>
/// The audit of the secrets of the top level (M6.18, design 12, docs/tasks/M6.md, from "Which views the audit records" to "The
/// audit screen"): each "show" and "copy" of a <c>secret</c> whose level in the record is the top level of <c>levels.json</c>,
/// whoever asks, is one line of <c>audit\&lt;day&gt;\&lt;account&gt;.jsonl</c>, appended straight to the share
/// (<see cref="DataCopy.AppendAudit"/>) after the value is decrypted and before it is given; a view that cannot be recorded
/// gives no value. The audit is for tracing only: anyone who writes the share can change or delete its lines.
/// </summary>
public static class Audit
{
    /// <summary>The longest span of a read in days.</summary>
    public const int MaxDays = 31;

    /// <summary>The latest end of a span: the read takes the dates through the one before it, and the last date has no next (M6.17).</summary>
    public static DateTime MaxTo { get; } = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>One line without indents, with only what JSON itself needs escaped, as in the documents (storage.md): it is never put into a page as it is.</summary>
    private static readonly JsonWriterOptions LineOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Whether a view of a secret at <paramref name="level"/> is recorded: the top level of <paramref name="levels"/> and above,
    /// which only a <c>levels.json</c> changed since the sign-in gives. Without a <c>levels.json</c> that fits, every protected
    /// level, as the top level is not known.
    /// </summary>
    public static bool IsAudited(int level, LevelsFile? levels) => level > 0 && (levels is null || level >= levels.Top);

    /// <summary>The use of a request, <c>show</c> or <c>copy</c>; null for anything else.</summary>
    public static SecretUse? UseOf(string? text) => text switch
    {
        "show" => SecretUse.Show,
        "copy" => SecretUse.Copy,
        _ => null,
    };

    /// <summary>The name of a use in the line and in the API.</summary>
    public static string NameOf(SecretUse use) => use == SecretUse.Show ? "show" : "copy";

    /// <summary>
    /// A time in UTC as Kiler writes its dates, such as <c>2026-10-31T10:00:00Z</c>, with up to 7 digits of a second, for the
    /// span of a read.
    /// </summary>
    public static bool TryReadTime(string? text, out DateTime time) => Authorship.TryReadDate(text, out time);

    /// <summary>
    /// The value of the <c>secret</c> <paramref name="key"/> of <paramref name="view"/> for <paramref name="session"/>
    /// (<see cref="RecordView.Secret"/>), recorded in the audit through <paramref name="copy"/> when its level
    /// <see cref="IsAudited"/>: the value is decrypted, then the line is appended, then the value is given. While the copy is
    /// offline, and when the append fails, nothing is written and the outcome is <see cref="SecretOutcome.NotRecorded"/>
    /// without a value. <paramref name="levels"/> is the <c>levels.json</c> of the request, null when it does not fit.
    /// </summary>
    /// <exception cref="SessionLockedException">The session is locked, before or while it decrypts.</exception>
    public static SecretOutcome Secret(
        RecordView view, string key, SecretUse use, Session session, LevelsFile? levels, DataCopy copy, TimeProvider time, out string? value)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(time);
        var outcome = view.Secret(key, session, out value);
        if (outcome != SecretOutcome.Value)
        {
            return outcome;
        }

        var level = view.Fields.First(field => string.Equals(field.Key, key, StringComparison.Ordinal)).Level;
        if (!IsAudited(level, levels))
        {
            return outcome;
        }

        var record = view.File.Model!;
        var at = time.GetUtcNow().UtcDateTime;
        var line = LineOf(new AuditEntry(at, session.Account, record.Id, record.Name, key, use, level));
        bool written;
        try
        {
            written = copy.AppendAudit(DateOnly.FromDateTime(at), session.Account, line) == WriteOutcome.Written;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A share that cannot be reached, a missing audit\ or a link (storage.md, "The audit"): the decision's 409.
            written = false;
        }

        if (written)
        {
            return outcome;
        }

        value = null;
        return SecretOutcome.NotRecorded;
    }

    /// <summary>Why a span from <paramref name="from"/> to <paramref name="to"/> cannot be read; null when it can.</summary>
    public static string? InvalidSpan(DateTime from, DateTime to)
    {
        if (from.Kind != DateTimeKind.Utc || to.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("A span of the audit is in UTC.", nameof(from));
        }

        return to <= from ? "to is not after from."
            : to - from > TimeSpan.FromDays(MaxDays) ? $"to is more than {MaxDays} days after from."
            : to > MaxTo ? "to is after the last date that Kiler reads."
            : null;
    }

    /// <summary>
    /// The entries with <paramref name="from"/> ≤ <c>at</c> &lt; <paramref name="to"/>, the newest first, read from the share
    /// at each call (<see cref="DataCopy.ReadAudit"/>): the files of the UTC dates of the span. A line counts only in the file of
    /// its account and the folder of its date, as Kiler writes it; another is a line that does not fit.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The span cannot be read (<see cref="InvalidSpan"/>).</exception>
    /// <exception cref="OfflineException">The copy is offline.</exception>
    /// <exception cref="IOException">The share cannot be reached, as for a load.</exception>
    public static AuditSpan Read(DataCopy copy, DateTime from, DateTime to)
    {
        ArgumentNullException.ThrowIfNull(copy);
        if (InvalidSpan(from, to) is { } reason)
        {
            throw new ArgumentOutOfRangeException(nameof(to), reason);
        }

        var read = copy.ReadAudit(DateOnly.FromDateTime(from), DateOnly.FromDateTime(to.AddTicks(-1)));
        var skipped = read.Skipped.Count + read.MoreSkipped;
        var entries = new List<AuditEntry>();
        foreach (var file in read.Files)
        {
            foreach (var line in file.Lines)
            {
                if (TryRead(line.Span, file.Day, file.Account) is not { } entry)
                {
                    skipped++;
                }
                else if (entry.At >= from && entry.At < to)
                {
                    entries.Add(entry);
                }
            }
        }

        // The files come day by day and account by account, each line after the one before it, so an equal time keeps that
        // order reversed.
        entries.Reverse();
        return new([.. entries.OrderByDescending(entry => entry.At)], skipped);
    }

    /// <summary>
    /// The line of <paramref name="entry"/> as UTF-8 without its <c>\n</c>, within <see cref="DataFolder.MaxAuditLine"/>: the
    /// name of a record has no cap of its own (M6.17), so a name too long is cut to the longest start that fits, never between
    /// the two halves of a surrogate pair.
    /// </summary>
    internal static byte[] LineOf(AuditEntry entry)
    {
        var line = Write(entry, entry.Name);
        if (line.Length <= DataFolder.MaxAuditLine)
        {
            return line;
        }

        // The start of fits characters fits, and the start of over does not.
        var (fits, over) = (0, entry.Name.Length);
        while (over - fits > 1)
        {
            var middle = fits + ((over - fits) / 2);
            if (Write(entry, StartOf(entry.Name, middle)).Length <= DataFolder.MaxAuditLine)
            {
                fits = middle;
            }
            else
            {
                over = middle;
            }
        }

        return Write(entry, StartOf(entry.Name, fits));
    }

    /// <summary>
    /// The entry of a line of the file of <paramref name="account"/> in the folder of <paramref name="day"/>, or null when it
    /// does not fit: not UTF-8 or not JSON, a member missing or of another type, a time that is not in UTC or not of that day,
    /// another account, a record that is not an <see cref="ItemId"/>, an empty field, a use other than show and copy, or a
    /// level outside 1 to <see cref="LevelsFile.MaxTop"/>.
    /// </summary>
    internal static AuditEntry? TryRead(ReadOnlySpan<byte> line, DateOnly day, string account)
    {
        if (!Utf8.IsValid(line))
        {
            return null;
        }

        AuditLineJson? json;
        try
        {
            json = JsonSerializer.Deserialize(line, CoreJsonContext.Default.AuditLineJson);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            // An escape that does not decode, such as a lone surrogate, is an InvalidOperationException, as in a group (core.md).
            return null;
        }

        return json is not null
            && Authorship.TryReadDate(json.At, out var at)
            && DateOnly.FromDateTime(at) == day
            && string.Equals(json.Account, account, StringComparison.Ordinal)
            && ItemId.IsValid(json.Record)
            && json.Name is not null
            && !string.IsNullOrEmpty(json.Field)
            && UseOf(json.Use) is { } use
            && json.Level is >= 1 and <= LevelsFile.MaxTop
            ? new AuditEntry(at, account, json.Record, json.Name, json.Field, use, json.Level.Value)
            : null;
    }

    private static byte[] Write(AuditEntry entry, string name)
    {
        var line = new AuditLineJson
        {
            At = Authorship.WriteDate(entry.At),
            Account = entry.Account,
            Record = entry.Record,
            Name = name,
            Field = entry.Field,
            Use = NameOf(entry.Use),
            Level = entry.Level,
        };
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, LineOptions))
        {
            JsonSerializer.Serialize(writer, line, CoreJsonContext.Default.AuditLineJson);
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static string StartOf(string name, int length) =>
        length > 0 && char.IsHighSurrogate(name[length - 1]) ? name[..(length - 1)] : name[..length];
}

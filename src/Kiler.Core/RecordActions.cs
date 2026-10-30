using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Kiler.Crypto;
using Kiler.Storage;

namespace Kiler.Core;

/// <summary>
/// The writes of an editor (M5.4, design 6.1 to 6.6 and 7.4): a new record and a new stash. A new record is checked against
/// its template (docs/api.md, <c>POST /api/records</c> and "Values by type"); each field takes its level ("Levels of a field"),
/// and each group of fields is encrypted with the key of its level and listed in <c>fields</c> (kiler-crypto, "Model"). A field
/// at a level whose key the author does not hold is refused, as nobody encrypts above their own level. Kiler sets the id, the
/// dates (<see cref="TimeProvider"/>, in UTC) and the authors. Each writes through <see cref="Records"/>, which takes the write
/// into its set, as no load gives it. The caller has checked <see cref="Need.Editor"/>.
/// </summary>
public static class RecordActions
{
    /// <summary>How many new ids a create takes when one meets <see cref="WriteOutcome.Exists"/>: with 40 random bits, a second one is already rare.</summary>
    private const int IdTries = 5;

    private const string TemplateMissing = "The template is not there, or cannot be used.";

    private const string NoSuchField = "The template has no field of this key.";

    private const string TooLarge = "The record is too large to be written.";

    /// <summary>
    /// The encoder of the shared documents (<see cref="DocumentFormat"/>): a group is never put into a page as it is, so only
    /// what JSON itself needs is escaped, and a Cyrillic value takes no more room in its group than in <c>public</c>.
    /// </summary>
    private static readonly JsonWriterOptions GroupOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// A new record of <paramref name="author"/> from <paramref name="draft"/>, with its template in <paramref name="files"/>,
    /// which the request read once, and the stashes and records of <paramref name="records"/> as the author reads them. The
    /// result has the record as the set of the author has it after the write.
    /// </summary>
    /// <exception cref="SessionLockedException">The session was locked before the write, or after it, before its record was read.</exception>
    public static RecordResult NewRecord(Session author, Records records, TeamFiles files, RecordDraft draft, TimeProvider time) =>
        NewRecord(author, records, files, draft, time, ItemId.New);

    /// <summary>
    /// A new stash of <paramref name="author"/> (design 6.6): its name trimmed and not blank, and unique without regard to case
    /// among the stashes of the copy, else <see cref="RecordOutcome.Exists"/>. Two of one name made at the same moment by two
    /// Kilers are both kept; an admin merges them after the exam (<see cref="StashFile"/>).
    /// </summary>
    /// <exception cref="SessionLockedException">The session is locked.</exception>
    public static RecordResult NewStash(Session author, Records records, string? name, TimeProvider time) =>
        NewStash(author, records, name, time, ItemId.New);

    /// <summary><see cref="NewRecord(Session, Records, TeamFiles, RecordDraft, TimeProvider)"/> with the ids of <paramref name="newId"/>, for the tests.</summary>
    internal static RecordResult NewRecord(Session author, Records records, TeamFiles files, RecordDraft draft, TimeProvider time, Func<string> newId)
    {
        ArgumentNullException.ThrowIfNull(author);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(time);
        var set = records.Read(author);
        if (draft.Template is null || files.Templates.GetValueOrDefault(draft.Template)?.Model is not { } template)
        {
            return Refused(null, TemplateMissing);
        }

        var name = draft.Name?.Trim() ?? "";
        List<string> stash = [];
        List<FieldEntry> values = [];
        if (Check(author, set, template, draft, name, stash, values) is { } refused)
        {
            return refused;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var authorship = new Authorship(author.Account, now, author.Account, now);
        var tags = Tags(draft.Tags);
        return Create(
            newId,
            id => Record(author, id, template.Id, authorship, stash, name, tags, values),
            records.Create,
            record => new(RecordOutcome.Done, Record: records.Read(author).Records.GetValueOrDefault(record.Id)));
    }

    /// <summary><see cref="NewStash(Session, Records, string?, TimeProvider)"/> with the ids of <paramref name="newId"/>, for the tests.</summary>
    internal static RecordResult NewStash(Session author, Records records, string? name, TimeProvider time, Func<string> newId)
    {
        ArgumentNullException.ThrowIfNull(author);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(time);
        var set = records.Read(author);
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return Refused("name", "The name of the stash is blank.");
        }

        if (set.Stashes.Values.Any(stash => string.Equals(stash.Model?.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return new(RecordOutcome.Exists);
        }

        var now = time.GetUtcNow().UtcDateTime;
        var authorship = new Authorship(author.Account, now, author.Account, now);
        return Create(newId, id => new StashFile(id, trimmed, authorship), records.Create, stash => new(RecordOutcome.Done, Stash: stash));
    }

    /// <summary>
    /// The checks of a new record that do not need its id (docs/api.md, <c>POST /api/records</c>), or <c>null</c> when it fits:
    /// the stash path, the name, the levels asked, and each field of the template with its value, its required mark and its
    /// level. Then <paramref name="stash"/> holds the ids of the path and <paramref name="values"/> each field that has a value,
    /// the description first, at its level.
    /// </summary>
    private static RecordResult? Check(Session author, RecordSet set, TemplateFile template, RecordDraft draft, string name, List<string> stash, List<FieldEntry> values)
    {
        if (Path(draft.Stash, set, stash) is { } path)
        {
            return Refused("stash", path);
        }

        if (name.Length == 0)
        {
            return Refused("name", "The name is blank.");
        }

        var fields = template.Fields.ToDictionary(field => field.Key, StringComparer.Ordinal);
        foreach (var (key, level) in draft.Levels)
        {
            if (key is "stash" or "name" or "tags" or TemplateField.Env)
            {
                return Refused(key, "The stash path, the name, the tags and the environment stay at level 0.");
            }

            if (key != RecordFile.Description && !fields.ContainsKey(key))
            {
                return Refused(null, NoSuchField);
            }

            if (level is < 0 or > LevelsFile.MaxTop)
            {
                return Refused(key, $"A level is not from 0 to {LevelsFile.MaxTop}.");
            }
        }

        if (draft.Fields.Keys.Any(key => !fields.ContainsKey(key)))
        {
            return Refused(null, NoSuchField);
        }

        if (!string.IsNullOrEmpty(draft.Description) && Add(author, RecordFile.Description, "The description", FieldValue.OfText(draft.Description), secret: false, 0, draft, values) is { } description)
        {
            return description;
        }

        // The environment of levelByEnv; a value that is not one of its options is refused below, before anything is written.
        var env = draft.Fields.GetValueOrDefault(TemplateField.Env) is { Kind: FieldValueKind.Text } envValue ? envValue.Text : null;
        foreach (var field in template.Fields)
        {
            var value = draft.Fields.GetValueOrDefault(field.Key);
            if (value is { Kind: FieldValueKind.Code, Text: "" })
            {
                // A code with no code is no value, as an empty text is not one (docs/api.md, "Values by type").
                value = null;
            }

            if (value is null)
            {
                if (field.Required)
                {
                    return Refused(field.Key, $"{field.Label} needs a value.");
                }

                continue;
            }

            if (Misfit(field, value, set) is { } misfit)
            {
                return Refused(field.Key, misfit);
            }

            if (Add(author, field.Key, field.Label, value, field.Type == FieldType.Secret, field.DefaultLevel(env), draft, values) is { } above)
            {
                return above;
            }
        }

        return null;
    }

    /// <summary>
    /// Adds a field with a value at its level: the higher of its default and the level asked ("Levels of a field"). A level
    /// whose key the author does not hold is refused, as the author cannot encrypt it; else the result is <c>null</c>.
    /// </summary>
    private static RecordResult? Add(Session author, string key, string label, FieldValue value, bool secret, int defaultLevel, RecordDraft draft, List<FieldEntry> values)
    {
        var level = Math.Max(defaultLevel, draft.Levels.GetValueOrDefault(key));
        if (!author.Reads(level))
        {
            return Refused(key, $"{label} would be at level {level}, which you do not hold.");
        }

        values.Add(new(key, value, secret, level));
        return null;
    }

    /// <summary>Why the stash path does not fit, or <c>null</c>, with its ids in <paramref name="stash"/>: 1 to 4 stashes that are there and can be read, without gaps or repeats (design 6.6).</summary>
    private static string? Path(IReadOnlyList<string?> path, RecordSet set, List<string> stash)
    {
        if (path.Count == 0)
        {
            return "The record needs a stash.";
        }

        if (path.Count > RecordFile.MaxStashes)
        {
            return $"The path has more than {RecordFile.MaxStashes} stashes.";
        }

        foreach (var id in path)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return "The path has a gap.";
            }

            if (set.Stashes.GetValueOrDefault(id) is not { IsValid: true })
            {
                return "A stash of the path is not there.";
            }

            if (stash.Contains(id, StringComparer.Ordinal))
            {
                return "The path has a stash twice.";
            }

            stash.Add(id);
        }

        return null;
    }

    /// <summary>Why <paramref name="value"/> does not fit the type of <paramref name="field"/>, or <c>null</c> (docs/api.md, "Values by type").</summary>
    private static string? Misfit(TemplateField field, FieldValue value, RecordSet set)
    {
        var label = field.Label;
        var text = value.Kind == FieldValueKind.Text ? value.Text : null;
        return field.Type switch
        {
            FieldType.Select => field.Options!.Contains(text, StringComparer.Ordinal) ? null : $"{label} must be one of its options.",
            FieldType.Number => value.Kind == FieldValueKind.Number ? null : $"{label} must be a number.",
            FieldType.Date => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? null : $"{label} must be a date, such as 2026-10-21.",
            FieldType.Url => Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? null
                : $"{label} must be an http or https address.",
            FieldType.Code when field.Languages is { } languages => value.Kind == FieldValueKind.Code && languages.Contains(value.Language, StringComparer.Ordinal)
                ? null
                : $"{label} must be a code in one of its languages.",
            FieldType.Ref => text is not null && set.Records.GetValueOrDefault(text)?.File.Model?.Template == field.RefTemplate
                ? null
                : $"{label} must point to a record of its template that is there.",
            _ => text is not null ? null : $"{label} must be a text.",
        };
    }

    /// <summary>The tags trimmed, without the blank ones, each once without regard to case, the first kept (docs/api.md, <c>POST /api/records</c>).</summary>
    private static List<string> Tags(IReadOnlyList<string?> tags)
    {
        var kept = new List<string>();
        foreach (var tag in tags)
        {
            var trimmed = tag?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && !kept.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                kept.Add(trimmed);
            }
        }

        return kept;
    }

    /// <summary>The record of <paramref name="id"/>: the fields of level 0 in <c>public</c>, and a group of each other level, encrypted for this id and listed in <c>fields</c>.</summary>
    /// <exception cref="SessionLockedException">The session was locked while a group was encrypted.</exception>
    private static RecordFile Record(Session author, string id, string template, Authorship authorship, List<string> stash, string name, List<string> tags, List<FieldEntry> values)
    {
        var publicFields = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
        var groups = new Dictionary<int, string>();
        var lists = new Dictionary<int, IReadOnlyList<string>>();
        foreach (var group in values.GroupBy(value => value.Level))
        {
            if (group.Key == 0)
            {
                foreach (var value in group)
                {
                    publicFields.Add(value.Key, value.Value);
                }

                continue;
            }

            groups.Add(group.Key, Encrypt(author, id, group.Key, group));
            lists.Add(group.Key, [.. group.Select(value => value.Key)]);
        }

        return new RecordFile(id, template, authorship, stash, name, tags, publicFields, groups, lists);
    }

    /// <summary>
    /// The group of <paramref name="level"/>: the UTF-8 of a JSON object of its fields, each <c>secret</c> as
    /// <c>{ "secret": "&lt;value&gt;" }</c> (kiler-crypto, "Model"), encrypted with the key of the level for the record
    /// <paramref name="id"/>. The bytes are zeroed once they are encrypted.
    /// </summary>
    private static string Encrypt(Session author, string id, int level, IEnumerable<FieldEntry> fields)
    {
        var utf8 = new ArrayBufferWriter<byte>();
        try
        {
            using (var writer = new Utf8JsonWriter(utf8, GroupOptions))
            {
                writer.WriteStartObject();
                foreach (var field in fields)
                {
                    writer.WritePropertyName(field.Key);
                    if (field.Secret)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("secret"u8, field.Value.Text);
                        writer.WriteEndObject();
                    }
                    else
                    {
                        field.Value.ToNode().WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            return author.Use(level, key => FieldGroup.Encrypt(key, utf8.WrittenSpan, id, level));
        }
        finally
        {
            // Clear zeroes the bytes written; a buffer that grew has left the copies of its start to the collector.
            utf8.Clear();
        }
    }

    /// <summary>
    /// Creates the model that <paramref name="make"/> makes for a new id through <paramref name="write"/>, with another id while
    /// one meets <see cref="WriteOutcome.Exists"/>, there or in the trash (core.md, "Calling the layer"); <paramref name="done"/>
    /// gives the result of the model written.
    /// </summary>
    private static RecordResult Create<T>(Func<string> newId, Func<string, T> make, Func<T, WriteResult> write, Func<T, RecordResult> done)
    {
        for (var tries = 0; tries < IdTries; tries++)
        {
            var model = make(newId());
            WriteResult written;
            try
            {
                written = write(model);
            }
            catch (ArgumentException)
            {
                // The layer refuses a content larger than a document can be (core.md, "Models"); a record is not nested deep.
                return Refused(null, TooLarge);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A share that cannot be reached before a load has said so (core.md, "Calling the layer").
                return new(RecordOutcome.NotWritten);
            }

            switch (written.Outcome)
            {
                case WriteOutcome.Exists:
                    continue;
                case WriteOutcome.Written:
                    return done(model);
                case WriteOutcome.Offline:
                    return new(RecordOutcome.Offline);
                case WriteOutcome.Busy:
                    return new(RecordOutcome.Busy);
                default:
                    return new(RecordOutcome.NotWritten);
            }
        }

        return new(RecordOutcome.NotWritten);
    }

    private static RecordResult Refused(string? field, string invalid) => new(RecordOutcome.Invalid, invalid, field);

    /// <summary>A field of a new record with a value, at its level; never printed, as a <c>secret</c> is among them.</summary>
    private readonly record struct FieldEntry(string Key, FieldValue Value, bool Secret, int Level)
    {
        public override string ToString() => Key;
    }
}

/// <summary>
/// A new record as the editor sends it (docs/api.md, <c>POST /api/records</c>). Its values are those of the request: the
/// name, the tags and the levels are checked and trimmed by <see cref="RecordActions"/>, never here.
/// </summary>
/// <param name="Template">The id of the template.</param>
/// <param name="Stash">The ids of the stashes of the path, from the first.</param>
/// <param name="Name">The name.</param>
/// <param name="Description">The description; <c>null</c> or empty is none.</param>
/// <param name="Tags">The tags.</param>
/// <param name="Fields">The values of the fields of the template, by key; a field left out, or <c>null</c>, has no value.</param>
/// <param name="Levels">The levels asked, by key: of the description or a field of the template ("Levels of a field").</param>
public sealed record RecordDraft(
    string? Template,
    IReadOnlyList<string?> Stash,
    string? Name,
    string? Description,
    IReadOnlyList<string?> Tags,
    IReadOnlyDictionary<string, FieldValue?> Fields,
    IReadOnlyDictionary<string, int> Levels)
{
    /// <summary>The name of the type: a record prints its properties, and this one holds the value of a <c>secret</c> (SKILL.md, "Style").</summary>
    public override string ToString() => nameof(RecordDraft);
}

/// <summary>How a write of an editor ended (<see cref="RecordActions"/>).</summary>
public enum RecordOutcome
{
    /// <summary>The record or the stash is written.</summary>
    Done,

    /// <summary>A value of the request does not fit; <see cref="RecordResult.Invalid"/> says why, and <see cref="RecordResult.Field"/> names it.</summary>
    Invalid,

    /// <summary>A new stash whose name a stash has, without regard to case.</summary>
    Exists,

    /// <summary>Another write of the file is under way, or a file of it is held: the write can be tried again in a moment.</summary>
    Busy,

    /// <summary>The shared folder cannot be reached (<see cref="WriteOutcome.Offline"/>): the write can be made once it is back.</summary>
    Offline,

    /// <summary>The file could not be written: the share did not answer or refused it, or every new id met one there.</summary>
    NotWritten,
}

/// <param name="Outcome">How the write ended.</param>
/// <param name="Invalid">Why a value does not fit, a sentence for the user, with <see cref="RecordOutcome.Invalid"/>; else null.</param>
/// <param name="Field">
/// The key of what does not fit: <c>stash</c>, <c>name</c>, <c>description</c> or a field of the template; null when it is none
/// of them, such as the template or a key that the template does not have (docs/api.md, "Conventions").
/// </param>
/// <param name="Record">The new record as the author reads it; null for a stash, a refusal, and a record that a load took away at once.</param>
/// <param name="Stash">The new stash; null for a record and a refusal.</param>
public sealed record RecordResult(RecordOutcome Outcome, string? Invalid = null, string? Field = null, RecordView? Record = null, StashFile? Stash = null);

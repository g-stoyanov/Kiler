using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Kiler.Crypto;
using Kiler.Storage;

namespace Kiler.Core;

/// <summary>
/// The writes of an editor (M5.4, design 6.1 to 6.6 and 7.4): a new record and a new stash; the edit (M5.8); the delete and
/// the restore from the trash (M5.10), which only an editor who holds every level of the record makes. A new record is
/// checked against its template (docs/api.md, <c>POST /api/records</c> and "Values by type"); each field takes its level
/// ("Levels of a field"), and each group of fields is encrypted with the key of its level and listed in <c>fields</c>
/// (kiler-crypto, "Model"). A field at a level whose key the author does not hold is refused, as nobody encrypts above their
/// own level. Kiler sets the id, the dates (<see cref="TimeProvider"/>, in UTC) and the authors. Each writes through
/// <see cref="Records"/>, which takes the write into its set, as no load gives it. The caller has checked <see cref="Need.Editor"/>.
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

    /// <summary>
    /// An edit of the record <paramref name="id"/> by <paramref name="editor"/> over <paramref name="version"/>, the version
    /// that the editor read (M5.8, docs/api.md, <c>PUT /api/records/{id}</c>): the partial edit of design 5.2.
    /// <paramref name="draft"/> holds the record as the editor wants it at the levels that they hold, checked as a new record
    /// is; its template is not read, as an edit keeps the template of the record. A field of those levels that the draft
    /// leaves out, or gives as null, has no value after it, but a <c>secret</c> left out keeps its value, as the page never
    /// has it, and one given as null is cleared. A value equal to the one that the record has is not checked again. The
    /// groups of the levels that the editor does not hold stay as they are, byte for byte, with their lists: a field of theirs
    /// in the draft is refused, also as null, and so is a field that would move across a level that the editor does not hold,
    /// by a change of <c>env</c>, a raise or a lowering. The creation stays; the editor and now are the last change. The
    /// result has the record as the editor reads it after the write, or, at a conflict, as it is on the share now.
    /// </summary>
    /// <exception cref="SessionLockedException">The session was locked before the write, or after it, before its record was read.</exception>
    public static RecordResult Edit(Session editor, Records records, string id, int version, RecordDraft draft, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(time);
        var set = records.Read(editor);
        if (set.Records.GetValueOrDefault(id) is not { File.Model: { } record } view)
        {
            return new(RecordOutcome.Missing);
        }

        if (view.File.Version != version)
        {
            return new(RecordOutcome.Conflict, Record: view);
        }

        if (view.Template is null)
        {
            return new(RecordOutcome.NoTemplate);
        }

        if (view.HeldSecrets(editor) is not { } held)
        {
            return new(RecordOutcome.Unreadable);
        }

        try
        {
            var name = draft.Name?.Trim() ?? "";
            List<string> stash = [];
            List<FieldEntry> values = [];
            if (CheckEdit(editor, set, view, held, draft, name, stash, values) is { } refused)
            {
                return refused;
            }

            var authorship = new Authorship(record.Authorship.CreatedBy, record.Authorship.CreatedAt, editor.Account, time.GetUtcNow().UtcDateTime);
            var edited = Record(editor, record.Id, record.Template, authorship, stash, name, Tags(draft.Tags), values, record);
            if (!TryWrite(() => records.Replace(edited, version), out var written, out var failed))
            {
                return failed;
            }

            return written.Outcome == WriteOutcome.Written
                ? new(RecordOutcome.Done, Record: records.Read(editor).Records.GetValueOrDefault(id))
                : NotWrittenOver(written, version, editor, records);
        }
        finally
        {
            RecordView.Zero(held);
        }
    }

    /// <summary>
    /// Moves the record <paramref name="id"/> to the trash over <paramref name="version"/>, the version that the editor read
    /// (M5.10, docs/api.md, <c>DELETE /api/records/{id}</c>), when <paramref name="editor"/> holds the level of every group of
    /// it ("Decided in M5.1"), else <see cref="RecordOutcome.NotHeld"/>. The file moves as it is, so its version, dates and
    /// authors stay (M2.5). A record whose template is missing or does not fit is deleted too, as its groups are in its file.
    /// The result has the record as the editor reads it in the trash after the delete, or, at a conflict, as it is now.
    /// </summary>
    /// <exception cref="SessionLockedException">The session was locked before the delete, or after it, before its record was read.</exception>
    public static RecordResult Delete(Session editor, Records records, string id, int version)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(id);
        var set = records.Read(editor);
        if (set.Records.GetValueOrDefault(id) is not { File.Model: { } record } view)
        {
            return new(RecordOutcome.Missing);
        }

        if (view.File.Version != version)
        {
            return new(RecordOutcome.Conflict, Record: view);
        }

        // The layer deletes only this version, so the groups checked are those that go to the trash.
        if (!record.Protected.Keys.All(editor.Reads))
        {
            return new(RecordOutcome.NotHeld);
        }

        if (!TryWrite(() => records.Delete(record, version), out var written, out var failed))
        {
            return failed;
        }

        return written.Outcome == WriteOutcome.Deleted
            ? new(RecordOutcome.Done, Record: records.Read(editor).Trash.GetValueOrDefault(id))
            : NotWrittenOver(written, version, editor, records);
    }

    /// <summary>
    /// Brings the record <paramref name="id"/> back from the trash (M5.10, docs/api.md, <c>POST /api/trash/{id}/restore</c>),
    /// when <paramref name="editor"/> holds the level of every group of it, as the trash of the set has it ("Decided in M5.1"),
    /// else <see cref="RecordOutcome.NotHeld"/>. A move, so its version, dates and authors stay (M2.5). The layer moves the file
    /// of the trash without a version, so another version that another Kiler deleted since the last load is restored as it
    /// is, with groups that may not have been checked (left for after the exam, docs/tasks/M5.md). A record that is not in the
    /// trash, also one that another Kiler has restored meanwhile, is <see cref="RecordOutcome.Missing"/>. The result has the
    /// record as the editor reads it after the restore.
    /// </summary>
    /// <exception cref="SessionLockedException">The session was locked before the restore, or after it, before its record was read.</exception>
    public static RecordResult Restore(Session editor, Records records, string id)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(id);
        var set = records.Read(editor);
        if (set.Trash.GetValueOrDefault(id) is not { File: { Model: { } record, Version: { } version } })
        {
            return new(RecordOutcome.Missing);
        }

        if (!record.Protected.Keys.All(editor.Reads))
        {
            return new(RecordOutcome.NotHeld);
        }

        if (!TryWrite(() => records.RestoreFromTrash(record, version), out var written, out var failed))
        {
            return failed;
        }

        return written.Outcome switch
        {
            WriteOutcome.Written => new(RecordOutcome.Done, Record: records.Read(editor).Records.GetValueOrDefault(id)),

            // Missing: it is no longer in the trash, as when another Kiler has restored it meanwhile; Exists: a record of its
            // id is live beside the one in the trash, which only a file put there by hand makes.
            WriteOutcome.Exists or WriteOutcome.Missing => new(RecordOutcome.Missing),
            _ => NotWritten(written),
        };
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
        if (CheckHeader(set, template, draft, name, stash) is { } header)
        {
            return header;
        }

        if (!string.IsNullOrEmpty(draft.Description) && Add(author, RecordFile.Description, "The description", FieldValue.OfText(draft.Description), secret: false, 0, draft, values) is { } description)
        {
            return description;
        }

        // The environment of levelByEnv; a value that is not one of its options is refused below, before anything is written.
        var env = EnvOf(draft.Fields.GetValueOrDefault(TemplateField.Env));
        foreach (var field in template.Fields)
        {
            var value = ValueOf(draft, field.Key);
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
    /// The checks of an edit (docs/api.md, <c>PUT /api/records/{id}</c>), or <c>null</c> when it fits, with the path in
    /// <paramref name="stash"/> and in <paramref name="values"/> each field of the levels of the editor that has a value after
    /// it, as <see cref="Check"/> gives them; the fields of the groups above the editor are not among them, as their groups
    /// stay. Each field takes its level as a new one does, with the <c>env</c> of the draft, and a raise counts as asked when
    /// the draft leaves out its level: a level above the default under the <c>env</c> that the record had ("Levels of a
    /// field"). <paramref name="held"/> has the JSON of each <c>secret</c> of the groups of the editor (<see cref="RecordView.HeldSecrets"/>).
    /// </summary>
    private static RecordResult? CheckEdit(Session editor, RecordSet set, RecordView view, Dictionary<string, byte[]> held, RecordDraft draft, string name, List<string> stash, List<FieldEntry> values)
    {
        var record = view.File.Model!;
        var template = view.Template!;
        if (CheckHeader(set, template, draft, name, stash) is { } header)
        {
            return header;
        }

        // The keys that the groups above the editor list; those groups stay, so no other part of the record may name them.
        var aboveLists = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (level, keys) in record.Fields.Where(group => !editor.Reads(group.Key)))
        {
            foreach (var key in keys)
            {
                aboveLists[key] = level;
            }
        }

        var description = string.IsNullOrEmpty(draft.Description) ? null : FieldValue.OfText(draft.Description);
        var oldEnv = EnvOf(record.Public.GetValueOrDefault(TemplateField.Env));
        var newEnv = EnvOf(draft.Fields.GetValueOrDefault(TemplateField.Env));
        var fields = template.Fields.Zip(view.Fields).Select(pair => (Field: (TemplateField?)pair.First, Current: pair.Second));
        foreach (var (field, current) in fields.Prepend((null, view.Description!)))
        {
            var key = current.Key;
            var label = field?.Label ?? "The description";
            var value = field is null ? description : ValueOf(draft, key);
            var given = field is null ? description is not null : draft.Fields.ContainsKey(key);
            var secret = field?.Type == FieldType.Secret || current.Kind == FieldKind.Secret;

            // The level asked, or the raise that the field has: a level above its default under the env that the record had.
            var defaultLevel = field?.DefaultLevel(newEnv) ?? 0;
            var raised = current.Kind != FieldKind.Empty && current.Level > (field?.DefaultLevel(oldEnv) ?? 0) ? current.Level : 0;
            var level = Math.Max(defaultLevel, draft.Levels.TryGetValue(key, out var asked) ? asked : raised);
            switch (current.Kind)
            {
                case FieldKind.Unreadable:
                    // HeldSecrets has decrypted every group of the editor, the same bytes with the same keys, so the view has
                    // none; the edit never writes over one.
                    return new(RecordOutcome.Unreadable);
                case FieldKind.Locked when given:
                    return Refused(key, $"{label} is at level {current.Level}, which you do not hold.");
                case FieldKind.Locked when level != current.Level:
                    return Refused(key, $"{label} would move from level {current.Level}, which you do not hold, to level {level}.");
                case FieldKind.Locked:
                    // It stays in its group.
                    continue;
            }

            byte[]? kept = null;
            if (value is null && secret && !given && current.Kind == FieldKind.Secret)
            {
                // A secret left out keeps its value: the JSON of its group, or a value in public, which a file changed by hand has.
                if (current.Level == 0)
                {
                    value = record.Public[key];
                }
                else
                {
                    kept = held[key];
                }
            }
            else if (value is null)
            {
                if (field is { Required: true })
                {
                    return Refused(key, $"{label} needs a value.");
                }

                continue;
            }
            else if (field is not null && !(current.Kind == FieldKind.Value && value.Equals(current.Value)) && Misfit(field, value, set) is { } misfit)
            {
                return Refused(key, misfit);
            }

            if (!editor.Reads(level))
            {
                return Refused(key, $"{label} would be at level {level}, which you do not hold.");
            }

            if (secret && level == 0)
            {
                return Refused(key, $"{label} is a secret, which is never at level 0.");
            }

            if (aboveLists.TryGetValue(key, out var above))
            {
                return Refused(key, $"{label} is also at level {above}, which you do not hold.");
            }

            values.Add(new(key, value, secret, level, kept));
        }

        return null;
    }

    /// <summary>
    /// The checks of the header of a new record or an edit, or <c>null</c> when it fits, with the ids of the path in
    /// <paramref name="stash"/>: the path, the name, the levels asked, and the keys of the fields.
    /// </summary>
    private static RecordResult? CheckHeader(RecordSet set, TemplateFile template, RecordDraft draft, string name, List<string> stash)
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

        return draft.Fields.Keys.Any(key => !fields.ContainsKey(key)) ? Refused(null, NoSuchField) : null;
    }

    /// <summary>The value of <paramref name="key"/> in the draft, or <c>null</c>: a code with no code is no value, as an empty text is not one (docs/api.md, "Values by type").</summary>
    private static FieldValue? ValueOf(RecordDraft draft, string key) =>
        draft.Fields.GetValueOrDefault(key) is { } value && !(value is { Kind: FieldValueKind.Code, Text: "" }) ? value : null;

    /// <summary>The environment of <c>levelByEnv</c> in a value of <c>env</c>: its text, or <c>null</c>.</summary>
    private static string? EnvOf(FieldValue? env) => env is { Kind: FieldValueKind.Text } ? env.Text : null;

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

    /// <summary>
    /// The record of <paramref name="id"/>: the fields of level 0 in <c>public</c>, and a group of each other level, encrypted
    /// for this id and listed in <c>fields</c>. An edit keeps the groups of <paramref name="edited"/> that the author does not
    /// hold, with their lists, as they are.
    /// </summary>
    /// <exception cref="SessionLockedException">The session was locked while a group was encrypted.</exception>
    private static RecordFile Record(Session author, string id, string template, Authorship authorship, List<string> stash, string name, List<string> tags, List<FieldEntry> values, RecordFile? edited = null)
    {
        var publicFields = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
        var groups = new Dictionary<int, string>();
        var lists = new Dictionary<int, IReadOnlyList<string>>();
        foreach (var group in values.GroupBy(value => value.Level))
        {
            if (group.Key == 0)
            {
                // A secret is never at level 0, so neither is a value kept as its JSON.
                foreach (var value in group)
                {
                    publicFields.Add(value.Key, value.Value!);
                }

                continue;
            }

            groups.Add(group.Key, Encrypt(author, id, group.Key, group));
            lists.Add(group.Key, [.. group.Select(value => value.Key)]);
        }

        foreach (var (level, group) in edited?.Protected ?? ImmutableDictionary<int, string>.Empty)
        {
            if (!author.Reads(level))
            {
                groups.Add(level, group);
                lists.Add(level, edited!.Fields[level]);
            }
        }

        return new RecordFile(id, template, authorship, stash, name, tags, publicFields, groups, lists);
    }

    /// <summary>
    /// The group of <paramref name="level"/>: the UTF-8 of a JSON object of its fields, each <c>secret</c> as
    /// <c>{ "secret": "&lt;value&gt;" }</c> (kiler-crypto, "Model"), the text of a number or a code when a group has marked a
    /// field of another type (M5.8), and a secret that an edit keeps as its JSON was, encrypted with the key of the level for
    /// the record <paramref name="id"/>. The bytes are zeroed once they are encrypted.
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
                    if (field.Kept is { } kept)
                    {
                        writer.WriteRawValue(kept);
                    }
                    else if (field.Secret)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("secret"u8, RecordView.TextOf(field.Value!));
                        writer.WriteEndObject();
                    }
                    else
                    {
                        field.Value!.ToNode().WriteTo(writer);
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
            if (!TryWrite(() => write(model), out var written, out var failed))
            {
                return failed;
            }

            switch (written.Outcome)
            {
                case WriteOutcome.Exists:
                    continue;
                case WriteOutcome.Written:
                    return done(model);
                default:
                    return NotWritten(written);
            }
        }

        return new(RecordOutcome.NotWritten);
    }

    /// <summary>
    /// Runs <paramref name="write"/>, or gives the refusal in <paramref name="failed"/> when the layer throws one before it
    /// writes: a content larger than a document can be (core.md, "Models"), and a share that cannot be reached before a load
    /// has said so (core.md, "Calling the layer").
    /// </summary>
    private static bool TryWrite(Func<WriteResult> write, [NotNullWhen(true)] out WriteResult? written, [NotNullWhen(false)] out RecordResult? failed)
    {
        failed = null;
        written = null;
        try
        {
            written = write();
            return true;
        }
        catch (ArgumentException)
        {
            // A record is not nested deep, so only its size is refused.
            failed = Refused(null, TooLarge);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed = new(RecordOutcome.NotWritten);
        }

        return false;
    }

    /// <summary>The result of a write that the layer did not make, for the outcomes that every write shares.</summary>
    private static RecordResult NotWritten(WriteResult written) => written.Outcome switch
    {
        WriteOutcome.Offline => new(RecordOutcome.Offline),
        WriteOutcome.Busy => new(RecordOutcome.Busy),
        _ => new(RecordOutcome.NotWritten),
    };

    /// <summary>
    /// The result of a write over <paramref name="version"/> that the layer did not make, an edit or a delete: at a conflict,
    /// the record on the share now, as <paramref name="session"/> reads it.
    /// </summary>
    private static RecordResult NotWrittenOver(WriteResult written, int version, Session session, Records records) => written.Outcome switch
    {
        // A slot in the way has the version that the write changes (core.md, "Calling the layer").
        WriteOutcome.Conflict when written.OnDisk!.Version == version => new(RecordOutcome.Busy),
        WriteOutcome.Conflict => new(RecordOutcome.Conflict, Record: records.ReadDocument(written.OnDisk, session)),
        WriteOutcome.Missing => new(RecordOutcome.Missing),
        _ => NotWritten(written),
    };

    private static RecordResult Refused(string? field, string invalid) => new(RecordOutcome.Invalid, invalid, field);

    /// <summary>
    /// A field with a value, at its level: a value, or the JSON of a <c>secret</c> that an edit keeps (<see cref="RecordView.HeldSecrets"/>);
    /// never printed, as a <c>secret</c> is among them.
    /// </summary>
    private readonly record struct FieldEntry(string Key, FieldValue? Value, bool Secret, int Level, byte[]? Kept = null)
    {
        public override string ToString() => Key;
    }
}

/// <summary>
/// A new record or an edit as the editor sends it (docs/api.md, <c>POST /api/records</c> and <c>PUT /api/records/{id}</c>).
/// Its values are those of the request: the name, the tags and the levels are checked and trimmed by
/// <see cref="RecordActions"/>, never here.
/// </summary>
/// <param name="Template">The id of the template; an edit does not read it.</param>
/// <param name="Stash">The ids of the stashes of the path, from the first.</param>
/// <param name="Name">The name.</param>
/// <param name="Description">The description; <c>null</c> or empty is none.</param>
/// <param name="Tags">The tags.</param>
/// <param name="Fields">
/// The values of the fields of the template, by key; a field left out, or <c>null</c>, has no value. In an edit, a
/// <c>secret</c> left out keeps its value, and one given as <c>null</c> is cleared.
/// </param>
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

    /// <summary>An edit or a delete over a version that is not the one there now; <see cref="RecordResult.Record"/> has the record as it is now (M5.8).</summary>
    Conflict,

    /// <summary>
    /// The record of an edit or a delete is not there, has gone meanwhile, or its file does not fit (M5.8); the record of a
    /// restore is not in the trash, or its file there does not fit (M5.10).
    /// </summary>
    Missing,

    /// <summary>The template of the record of an edit is missing or does not fit, so its fields cannot be told apart (M5.8).</summary>
    NoTemplate,

    /// <summary>A group of a level of the editor does not decrypt, or what it decrypts to does not fit, so an edit would write over it (M5.8).</summary>
    Unreadable,

    /// <summary>A delete or a restore of a record with a group at a level that the editor does not hold ("Decided in M5.1", M5.10).</summary>
    NotHeld,
}

/// <param name="Outcome">How the write ended.</param>
/// <param name="Invalid">Why a value does not fit, a sentence for the user, with <see cref="RecordOutcome.Invalid"/>; else null.</param>
/// <param name="Field">
/// The key of what does not fit: <c>stash</c>, <c>name</c>, <c>description</c> or a field of the template; null when it is none
/// of them, such as the template or a key that the template does not have (docs/api.md, "Conventions").
/// </param>
/// <param name="Record">
/// The new, edited or restored record as the author reads it, a deleted one as it is in the trash, and at
/// <see cref="RecordOutcome.Conflict"/> the record as it is now; null for a stash, the other refusals, and a record that a
/// load took away at once.
/// </param>
/// <param name="Stash">The new stash; null for a record and a refusal.</param>
public sealed record RecordResult(RecordOutcome Outcome, string? Invalid = null, string? Field = null, RecordView? Record = null, StashFile? Stash = null);

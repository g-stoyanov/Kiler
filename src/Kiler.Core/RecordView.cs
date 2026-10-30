using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Unicode;
using Kiler.Crypto;

namespace Kiler.Core;

/// <summary>What a session reads of a field of a record (docs/api.md, "Field").</summary>
public enum FieldKind
{
    /// <summary>The session reads it, and <see cref="RecordField.Value"/> holds it.</summary>
    Value,

    /// <summary>The field has no value.</summary>
    Empty,

    /// <summary>A <c>secret</c> with a value, which is not kept: only the request of a secret decrypts it again (M5.7).</summary>
    Secret,

    /// <summary>The field is at a level whose key the session does not hold.</summary>
    Locked,

    /// <summary>Its group does not decrypt, or what it decrypts to does not fit: damaged, or moved from another record or level.</summary>
    Unreadable,
}

/// <summary>A field of a record as a session reads it: its key, its level in this record, what the session reads of it, and the value when it reads one.</summary>
public sealed class RecordField
{
    internal RecordField(string key, int level, FieldKind kind, FieldValue? value)
    {
        Key = key;
        Level = level;
        Kind = kind;
        Value = value;
    }

    public string Key { get; }

    /// <summary>
    /// Its group, or 0 in <c>public</c>; for a field without a value, the level that a value would get
    /// (<see cref="TemplateField.DefaultLevel"/>).
    /// </summary>
    public int Level { get; }

    public FieldKind Kind { get; }

    /// <summary>The value of a <see cref="FieldKind.Value"/>; null for the other kinds, a <c>secret</c> among them.</summary>
    public FieldValue? Value { get; }

    public override string ToString() => $"{Key} ({Level}, {Kind})";
}

/// <summary>
/// A record as a session reads it (M5.3, design 5.3): its file, and its fields in the order of its template, each with what
/// the session reads of it. A group of a level that the session holds is decrypted (<see cref="Session.Use"/>) and is the
/// truth; the fields of the other groups are <see cref="FieldKind.Locked"/> at their level, by the lists of
/// <see cref="RecordFile.Fields"/>; a guest reads level 0. A field is a <c>secret</c> when its template says so or its group
/// marks it (<see cref="FieldValue.IsSecret"/>); it keeps only that it has a value, and its value never becomes a string
/// here. A record whose template is missing or does not fit keeps only its header and the description, as Kiler cannot tell
/// which of its fields is a <c>secret</c> (docs/api.md, "Record").
/// </summary>
public sealed class RecordView
{
    private RecordView(TeamFile<RecordFile> file, TeamFile<TemplateFile>? templateEntry, RecordField? description, IReadOnlyList<RecordField> fields)
    {
        File = file;
        TemplateEntry = templateEntry;
        Description = description;
        Fields = fields;
    }

    /// <summary>The file: its version, and its model or why it does not fit.</summary>
    public TeamFile<RecordFile> File { get; }

    /// <summary>The template of <see cref="Fields"/>; null when the record does not fit, or its template is missing or does not fit.</summary>
    public TemplateFile? Template => File.IsValid ? TemplateEntry?.Model : null;

    /// <summary>The description as a field; null when the record does not fit.</summary>
    public RecordField? Description { get; }

    /// <summary>Every field of <see cref="Template"/>, in its order; empty without one.</summary>
    public IReadOnlyList<RecordField> Fields { get; }

    /// <summary>The entry of <c>templates\</c> that the view was read with, so a change of it reads the record again (<see cref="Records"/>).</summary>
    internal TeamFile<TemplateFile>? TemplateEntry { get; }

    /// <summary>
    /// Reads <paramref name="file"/> with its template in <paramref name="templates"/> for <paramref name="session"/>, or for a
    /// guest when it is null. A group that does not decrypt, or a lock while it reads, leaves the rest of the record.
    /// </summary>
    internal static RecordView Read(TeamFile<RecordFile> file, IReadOnlyDictionary<string, TeamFile<TemplateFile>> templates, Session? session)
    {
        if (file.Model is not { } record)
        {
            return new(file, null, null, []);
        }

        var entry = templates.GetValueOrDefault(record.Template);
        var template = entry?.Model;

        // The keys that the view reads, and whether each is a secret: the description, and the fields of the template.
        var secrets = new Dictionary<string, bool>(StringComparer.Ordinal) { [RecordFile.Description] = false };
        foreach (var field in template?.Fields ?? [])
        {
            secrets[field.Key] = field.Type == FieldType.Secret;
        }

        // Each key at the highest level that has it: a group that a key holder wrote takes the place of public, which anyone
        // can write, and of a lower group. Kiler writes each key once, so only a file changed by hand has one twice.
        var found = new Dictionary<string, RecordField>(StringComparer.Ordinal);
        foreach (var (key, value) in record.Public)
        {
            if (secrets.TryGetValue(key, out var secret))
            {
                found[key] = secret ? new(key, 0, FieldKind.Secret, null) : new(key, 0, FieldKind.Value, value);
            }
        }

        foreach (var (level, group) in record.Protected)
        {
            var kind = FieldKind.Locked;
            if (session is not null && session.Reads(level))
            {
                if (Decrypt(session, record.Id, level, group, secrets) is { } fields)
                {
                    foreach (var (key, field) in fields)
                    {
                        found[key] = field;
                    }

                    continue;
                }

                kind = session.IsLocked ? FieldKind.Locked : FieldKind.Unreadable;
            }

            foreach (var key in record.Fields[level])
            {
                if (secrets.ContainsKey(key))
                {
                    found[key] = new(key, level, kind, null);
                }
            }
        }

        var description = found.GetValueOrDefault(RecordFile.Description) ?? new(RecordFile.Description, 0, FieldKind.Empty, null);
        if (template is null)
        {
            return new(file, entry, description, []);
        }

        // The environment of levelByEnv is that of public, as env stays at level 0 (docs/api.md, "Levels of a field").
        var env = record.Public.GetValueOrDefault(TemplateField.Env) is { Kind: FieldValueKind.Text } envValue ? envValue.Text : null;
        return new(file, entry, description, [.. template.Fields.Select(field => found.GetValueOrDefault(field.Key) ?? new(field.Key, field.DefaultLevel(env), FieldKind.Empty, null))]);
    }

    /// <summary>
    /// The fields of the group of <paramref name="level"/> that the view reads, or <c>null</c> when it does not decrypt, what it
    /// decrypts to does not fit, or the session is locked. The decrypted bytes are zeroed once they are parsed (core.md).
    /// </summary>
    private static Dictionary<string, RecordField>? Decrypt(Session session, string id, int level, string group, Dictionary<string, bool> secrets)
    {
        byte[] utf8;
        try
        {
            utf8 = session.Use(level, key => FieldGroup.Decrypt(key, group, id, level));
        }
        catch (Exception exception) when (exception is CryptographicException or SessionLockedException)
        {
            return null;
        }

        try
        {
            // The parse leaves the strings as the bytes have them, as for a document of the share (DocumentFormat): bytes that
            // are not UTF-8 are refused first, and a name or a text whose escape does not decode, such as a lone surrogate,
            // throws InvalidOperationException at its first read. Neither leaves the group: it does not fit.
            if (!Utf8.IsValid(utf8))
            {
                return null;
            }

            // Over the bytes as they are, without a copy, so the zeroing below reaches every one.
            using var document = JsonDocument.Parse(utf8.AsMemory());
            return ReadGroup(document.RootElement, level, secrets);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
        }
    }

    /// <summary>
    /// The fields of a decrypted group that the view reads, or <c>null</c> when it does not fit: not an object, a key twice, or
    /// a value of no shape of <see cref="FieldValue"/>. A field is a <c>secret</c> when its template says so or the group marks
    /// it (<see cref="FieldValue.IsSecret"/>), never fewer, and a <c>secret</c> is checked without reading its text.
    /// </summary>
    private static Dictionary<string, RecordField>? ReadGroup(JsonElement group, int level, Dictionary<string, bool> secrets)
    {
        if (group.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var fields = new Dictionary<string, RecordField>(StringComparer.Ordinal);
        foreach (var member in group.EnumerateObject())
        {
            var key = member.Name;
            if (!keys.Add(key))
            {
                return null;
            }

            if (FieldValue.IsSecret(member.Value, out var marked))
            {
                if (marked && secrets.ContainsKey(key))
                {
                    fields[key] = new(key, level, FieldKind.Secret, null);
                }
            }
            else if (!secrets.TryGetValue(key, out var secret) || secret)
            {
                if (!FieldValue.Fits(member.Value, out var isValue))
                {
                    return null;
                }

                if (secret && isValue)
                {
                    fields[key] = new(key, level, FieldKind.Secret, null);
                }
            }
            else
            {
                if (!FieldValue.TryRead(member.Value, out var value))
                {
                    return null;
                }

                if (value is not null)
                {
                    fields[key] = new(key, level, FieldKind.Value, value);
                }
            }
        }

        return fields;
    }
}

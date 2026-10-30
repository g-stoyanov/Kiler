using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Kiler.Storage;

namespace Kiler.Core;

/// <summary>The type of a field of a template (design 6.4); its name in the JSON is <see cref="TemplateField.NameOf"/>.</summary>
public enum FieldType
{
    Text,
    Multiline,
    Markdown,
    Doc,
    Select,
    Number,
    Date,
    Url,
    Code,
    Ref,
    Secret,
}

/// <summary>
/// A field of a template (M5.2, design 6.2 to 6.5): its key, label and type, its default level or the levels by the
/// environment of the record, and the settings that belong to its type. A setting of another type does not fit, so a
/// template says nothing that Kiler ignores. The rules that need the other fields, such as a key once in the template and
/// <c>levelByEnv</c> beside <see cref="Env"/>, are the template's (<see cref="TemplateFile"/>).
/// </summary>
public sealed class TemplateField
{
    public const int MaxKeyLength = 40;

    public const int MaxLabelLength = 100;

    public const int MaxOptionLength = 100;

    public const int MaxLanguageLength = 40;

    /// <summary>
    /// The key of the field whose value <c>levelByEnv</c> reads (design 6.2). It stays at level 0, as the stash path, the name
    /// and the tags do (docs/api.md, "Levels of a field").
    /// </summary>
    public const string Env = "env";

    private static readonly string[] TypeNames = ["text", "multiline", "markdown", "doc", "select", "number", "date", "url", "code", "ref", "secret"];

    private static readonly SearchValues<char> KeyCharacters = SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");

    /// <param name="key">The key in the record, 1 to 40 ASCII letters and digits from a lowercase letter, none of <see cref="Header"/>.</param>
    /// <param name="label">The label in the form and the view.</param>
    /// <param name="type">The type.</param>
    /// <param name="level">The default level; null when <paramref name="levelByEnv"/> alone gives it.</param>
    /// <param name="levelByEnv">The level by the value of <see cref="Env"/> in the record, such as the password of <c>prod</c> at 3.</param>
    /// <param name="required">Whether a record needs a value.</param>
    /// <param name="filter">Whether the field is a filter of the list.</param>
    /// <param name="list">Whether the list shows the field.</param>
    /// <param name="options">The values of a <see cref="FieldType.Select"/>.</param>
    /// <param name="language">The fixed language of a <see cref="FieldType.Code"/>.</param>
    /// <param name="dialect">The dialect of <paramref name="language"/>.</param>
    /// <param name="languages">The languages that a <see cref="FieldType.Code"/> takes; the record keeps the one chosen with the code (design 6.3).</param>
    /// <param name="refTemplate">The template of the records that a <see cref="FieldType.Ref"/> points to.</param>
    /// <exception cref="ArgumentException">A value does not fit; the message says why.</exception>
    public TemplateField(
        string key,
        string label,
        FieldType type,
        int? level = null,
        IReadOnlyDictionary<string, int>? levelByEnv = null,
        bool required = false,
        bool filter = false,
        bool list = false,
        IReadOnlyList<string>? options = null,
        string? language = null,
        string? dialect = null,
        IReadOnlyList<string>? languages = null,
        string? refTemplate = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(label);
        if (Invalid(key, label, type, level, levelByEnv, filter, list, options, language, dialect, languages, refTemplate) is { } reason)
        {
            throw new ArgumentException(reason);
        }

        Key = key;
        Label = label;
        Type = type;
        Level = level;
        LevelByEnv = levelByEnv is null ? null : new Dictionary<string, int>(levelByEnv, StringComparer.Ordinal);
        Required = required;
        Filter = filter;
        List = list;
        Options = options is null ? null : [.. options];
        Language = language;
        Dialect = dialect;
        Languages = languages is null ? null : [.. languages];
        RefTemplate = refTemplate;
    }

    /// <summary>The members of the header of a record (design 6.1), which no field takes, without regard to case.</summary>
    public static IReadOnlyList<string> Header { get; } = ["stash", "name", "description", "tags", "createdAt", "createdBy", "updatedAt", "updatedBy", "private"];

    public string Key { get; }

    public string Label { get; }

    public FieldType Type { get; }

    /// <summary>The default level, 0 to <see cref="LevelsFile.MaxTop"/>; null when <see cref="LevelByEnv"/> alone gives it.</summary>
    public int? Level { get; }

    /// <summary>The level by the value of <see cref="Env"/>, whose options are its keys; null when the field has none.</summary>
    public IReadOnlyDictionary<string, int>? LevelByEnv { get; }

    public bool Required { get; }

    public bool Filter { get; }

    public bool List { get; }

    /// <summary>The values of a <see cref="FieldType.Select"/>, each once without regard to case; null for another type.</summary>
    public IReadOnlyList<string>? Options { get; }

    /// <summary>The fixed language of a <see cref="FieldType.Code"/>; null for another type, and beside <see cref="Languages"/>.</summary>
    public string? Language { get; }

    /// <summary>The dialect of <see cref="Language"/>, such as <c>plsql</c> of <c>sql</c>; null when there is none.</summary>
    public string? Dialect { get; }

    /// <summary>The languages that a <see cref="FieldType.Code"/> takes, each once without regard to case; null beside <see cref="Language"/>.</summary>
    public IReadOnlyList<string>? Languages { get; }

    /// <summary>The id of the template whose records a <see cref="FieldType.Ref"/> points to; null for another type.</summary>
    public string? RefTemplate { get; }

    /// <summary>The name of the type in the JSON of the template and of the API, such as <c>secret</c>.</summary>
    public static string NameOf(FieldType type) => TypeNames[(int)type];

    /// <summary>
    /// The default level of the field in a record whose <see cref="Env"/> is <paramref name="env"/> (docs/api.md, "Levels of a
    /// field"): its <see cref="LevelByEnv"/> of that value; else its <see cref="Level"/>; else the highest of its
    /// <see cref="LevelByEnv"/>, so an <see cref="Env"/> left empty does not lower a password.
    /// </summary>
    public int DefaultLevel(string? env) =>
        LevelByEnv is { } byEnv && env is not null && byEnv.TryGetValue(env, out var level) ? level : Level ?? LevelByEnv?.Values.Max() ?? 0;

    /// <summary>The field of an entry of <c>fields</c> in the JSON of a template, or why it does not fit, with the number of the entry.</summary>
    internal static bool TryRead(TemplateFieldJson? json, int index, [NotNullWhen(true)] out TemplateField? field, [NotNullWhen(false)] out string? invalid)
    {
        field = null;
        var entry = $"Entry {index} of fields";
        if (json is null)
        {
            invalid = $"{entry} is null.";
            return false;
        }

        if (json.Key is not { } key)
        {
            invalid = $"{entry} has no key.";
            return false;
        }

        if (json.Label is not { } label)
        {
            invalid = $"{entry} has no label.";
            return false;
        }

        var type = Array.IndexOf(TypeNames, json.Type);
        if (type < 0)
        {
            invalid = $"{entry}: type is missing or is not one of {string.Join(", ", TypeNames)}.";
            return false;
        }

        if (json.LevelByEnv?.ContainsValue(null) == true || json.Options?.Contains(null) == true || json.Languages?.Contains(null) == true)
        {
            invalid = $"{entry}: an entry of levelByEnv, options or languages is null.";
            return false;
        }

        var levelByEnv = json.LevelByEnv?.ToDictionary(pair => pair.Key, pair => pair.Value.GetValueOrDefault(), StringComparer.Ordinal);
        var options = json.Options?.OfType<string>().ToList();
        var languages = json.Languages?.OfType<string>().ToList();
        var filter = json.Filter ?? false;
        var list = json.List ?? false;
        if (Invalid(key, label, (FieldType)type, json.Level, levelByEnv, filter, list, options, json.Language, json.Dialect, languages, json.RefTemplate) is { } reason)
        {
            invalid = $"{entry}: {reason}";
            return false;
        }

        field = new TemplateField(key, label, (FieldType)type, json.Level, levelByEnv, json.Required ?? false, filter, list, options, json.Language, json.Dialect, languages, json.RefTemplate);
        invalid = null;
        return true;
    }

    /// <summary>The entry of <c>fields</c> in the JSON of the template; a setting that is false or null is left out.</summary>
    internal TemplateFieldJson ToJson() => new()
    {
        Key = Key,
        Label = Label,
        Type = NameOf(Type),
        Options = Options is null ? null : [.. Options],
        Language = Language,
        Dialect = Dialect,
        Languages = Languages is null ? null : [.. Languages],
        RefTemplate = RefTemplate,
        Level = Level,
        LevelByEnv = LevelByEnv?.ToDictionary(pair => pair.Key, pair => (int?)pair.Value, StringComparer.Ordinal),
        Required = Required ? true : null,
        Filter = Filter ? true : null,
        List = List ? true : null,
    };

    /// <summary>Whether a text of the template is null, blank or longer than <paramref name="maxLength"/>.</summary>
    internal static bool IsBlankOrLong([NotNullWhen(false)] string? text, int maxLength) =>
        text is null || text.Length > maxLength || string.IsNullOrWhiteSpace(text);

    private static string? Invalid(
        string key,
        string label,
        FieldType type,
        int? level,
        IReadOnlyDictionary<string, int>? levelByEnv,
        bool filter,
        bool list,
        IReadOnlyList<string>? options,
        string? language,
        string? dialect,
        IReadOnlyList<string>? languages,
        string? refTemplate)
    {
        if (!Enum.IsDefined(type))
        {
            return "type is not one of design 6.4.";
        }

        if (key.Length is 0 or > MaxKeyLength || key[0] is < 'a' or > 'z' || key.AsSpan().ContainsAnyExcept(KeyCharacters))
        {
            return $"key is not 1 to {MaxKeyLength} ASCII letters and digits that start with a lowercase letter.";
        }

        if (Header.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            return $"key is a member of the header of a record ({string.Join(", ", Header)}), without regard to case.";
        }

        if (IsBlankOrLong(label, MaxLabelLength))
        {
            return $"label is blank or longer than {MaxLabelLength} characters.";
        }

        if (Levels(type, level, levelByEnv) is { } levels)
        {
            return levels;
        }

        if (key == Env && (level != 0 || levelByEnv is not null))
        {
            return "The field env is not at level 0 alone, though it stays there.";
        }

        if (type == FieldType.Secret && (list || filter))
        {
            return "A secret has list or filter, though it is never in the list or the search.";
        }

        var select = type == FieldType.Select ? Values(options, "options", MaxOptionLength)
            : options is null ? null
            : "options is only for a select.";
        if (select is not null)
        {
            return select;
        }

        if (Code(type, language, dialect, languages) is { } code)
        {
            return code;
        }

        return type == FieldType.Ref
            ? StorageName.IsValid(refTemplate) ? null : "refTemplate is missing or is not the id of a template."
            : refTemplate is null ? null : "refTemplate is only for a ref.";
    }

    private static string? Levels(FieldType type, int? level, IReadOnlyDictionary<string, int>? levelByEnv)
    {
        if (level is null && levelByEnv is null)
        {
            return "The field has neither level nor levelByEnv.";
        }

        if (level is < 0 or > LevelsFile.MaxTop || levelByEnv?.Values.Any(byEnv => byEnv is < 0 or > LevelsFile.MaxTop) == true)
        {
            return $"A level of the field is not from 0 to {LevelsFile.MaxTop}.";
        }

        if (levelByEnv?.Count == 0)
        {
            return "levelByEnv is empty.";
        }

        // Design 7.1: passwords are at level 1 or higher, as level 0 is in clear.
        return type == FieldType.Secret && (level < 1 || levelByEnv?.Values.Any(byEnv => byEnv < 1) == true) ? "A secret is below level 1." : null;
    }

    private static string? Code(FieldType type, string? language, string? dialect, IReadOnlyList<string>? languages)
    {
        if (type != FieldType.Code)
        {
            return language is null && dialect is null && languages is null ? null : "language, dialect and languages are only for code.";
        }

        if ((language is null) == (languages is null))
        {
            return "A code field has either language or languages, not both or neither.";
        }

        if (language is not null && IsBlankOrLong(language, MaxLanguageLength))
        {
            return $"language is blank or longer than {MaxLanguageLength} characters.";
        }

        if (dialect is not null && (language is null || IsBlankOrLong(dialect, MaxLanguageLength)))
        {
            return $"dialect is not beside language, or is blank or longer than {MaxLanguageLength} characters.";
        }

        return languages is null ? null : Values(languages, "languages", MaxLanguageLength);
    }

    /// <summary>Why a list of values does not fit: missing or empty, a value blank or too long, or one twice without regard to case.</summary>
    private static string? Values(IReadOnlyList<string>? values, string name, int maxLength)
    {
        if (values is null || values.Count == 0)
        {
            return $"{name} is missing or empty.";
        }

        if (values.Any(value => IsBlankOrLong(value, maxLength)))
        {
            return $"An entry of {name} is blank or longer than {maxLength} characters.";
        }

        return values.Distinct(StringComparer.OrdinalIgnoreCase).Count() == values.Count ? null : $"An entry of {name} is there twice, without regard to case.";
    }
}

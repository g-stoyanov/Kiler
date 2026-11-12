using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Kiler.Crypto;
using Kiler.Storage;

namespace Kiler.Core;

/// <summary>
/// <c>templates\&lt;id&gt;.json</c> (M5.2, design 6): the name and the icon of a kind of record, and its fields, which come
/// after the header of the record (design 6.1). The id is the document's. Until the exam the templates are read only: the
/// wizard writes the initial ones (<see cref="InitialTemplates"/>), and an admin changes them after it (decided at the start
/// of M5). It is on the share, so a content that does not fit is invalid, with a reason
/// (<see cref="TryRead(Document, out TemplateFile?, out string?)"/>), and a record of it shows only its header (docs/api.md).
/// Its <c>stamp</c> is the admin seal of the whole file, with its id and version (M4.11, <see cref="IsSealedBy"/>).
/// </summary>
public sealed class TemplateFile
{
    public const int MaxNameLength = 100;

    public const int MaxIconLength = 40;

    private readonly FileSeal? _seal;

    /// <param name="id">The id of the document, which must pass <see cref="StorageName"/>.</param>
    /// <param name="name">The name of the kind of record.</param>
    /// <param name="icon">The name of the icon, which the page takes from its own set.</param>
    /// <param name="fields">The fields, in the order of the form and the view.</param>
    /// <exception cref="ArgumentException">A value does not fit; the message says why.</exception>
    public TemplateFile(string id, string name, string icon, IReadOnlyList<TemplateField> fields)
        : this(id, name, icon, fields, seal: null)
    {
    }

    private TemplateFile(string id, string name, string icon, IReadOnlyList<TemplateField> fields, FileSeal? seal)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(icon);
        ArgumentNullException.ThrowIfNull(fields);
        StorageName.Check(id);
        if (Invalid(name, icon, fields) is { } reason)
        {
            throw new ArgumentException(reason);
        }

        Id = id;
        Name = name;
        Icon = icon;
        Fields = [.. fields];
        _seal = seal;
    }

    public string Id { get; }

    public DocumentKey Key => KeyOf(Id);

    public string Name { get; }

    public string Icon { get; }

    /// <summary>The fields, each key once without regard to case, in the order of the file.</summary>
    public IReadOnlyList<TemplateField> Fields { get; }

    /// <summary>The <c>stamp</c> of the file, or <c>null</c>; it may not be a seal at all (<see cref="IsSealedBy"/>).</summary>
    public string? Stamp => _seal?.Stamp;

    /// <summary>The template of <paramref name="id"/>, which must pass <see cref="StorageName"/>.</summary>
    public static DocumentKey KeyOf(string id) => DocumentKey.InFolder(DataFolder.Templates, id);

    /// <summary>The model of the document, or why its content does not fit.</summary>
    /// <exception cref="ArgumentException">The document is not in <c>templates\</c> of the data folder.</exception>
    public static bool TryRead(Document document, [NotNullWhen(true)] out TemplateFile? template, [NotNullWhen(false)] out string? invalid)
    {
        ArgumentNullException.ThrowIfNull(document);
        return TryRead(document.Key, document.Version, document.Content, out template, out invalid);
    }

    /// <summary>
    /// Whether <see cref="Stamp"/> is the seal of the file as it was read (<see cref="FileSeal"/>) by the admin key of
    /// <paramref name="publicKey"/>, as <see cref="LevelsFile.IsSealedBy"/>.
    /// </summary>
    public bool IsSealedBy(string publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        return _seal?.IsBy(publicKey) == true;
    }

    /// <summary>The file sealed by <paramref name="adminKey"/> for <paramref name="version"/>, as <see cref="LevelsFile.Sealed"/>.</summary>
    /// <exception cref="ObjectDisposedException">The admin key is disposed.</exception>
    public TemplateFile Sealed(AdminKey adminKey, int version)
    {
        ArgumentNullException.ThrowIfNull(adminKey);
        return new(Id, Name, Icon, Fields, FileSeal.Make(adminKey, SealPurpose.Template, Key, version, Content(stamp: null)));
    }

    /// <summary>The content of the document, without <c>id</c> and <c>version</c>, which the layer adds, and with its <c>stamp</c>.</summary>
    public JsonObject ToContent() => Content(Stamp);

    internal static bool TryRead(DocumentKey key, int version, JsonObject content, [NotNullWhen(true)] out TemplateFile? template, [NotNullWhen(false)] out string? invalid)
    {
        if (key.Account is not null || key.Folder != DataFolder.Templates)
        {
            throw new ArgumentException("The document is not a template.", nameof(key));
        }

        template = null;
        if (ModelJson.Read(content, CoreJsonContext.Default.TemplateJson) is not { } json)
        {
            invalid = ModelJson.WrongType;
            return false;
        }

        if (json.Name is not { } name)
        {
            invalid = "name is missing.";
            return false;
        }

        if (json.Icon is not { } icon)
        {
            invalid = "icon is missing.";
            return false;
        }

        if (json.Fields is null)
        {
            invalid = "fields is missing.";
            return false;
        }

        var fields = new List<TemplateField>(json.Fields.Count);
        for (var i = 0; i < json.Fields.Count; i++)
        {
            if (!TemplateField.TryRead(json.Fields[i], i, out var field, out invalid))
            {
                return false;
            }

            fields.Add(field);
        }

        invalid = Invalid(name, icon, fields);
        if (invalid is not null)
        {
            return false;
        }

        template = new TemplateFile(key.Id, name, icon, fields, FileSeal.Read(SealPurpose.Template, key, version, content, json.Stamp));
        return true;
    }

    /// <summary>Why the template does not fit as a whole, or <c>null</c>; each field has passed its own check.</summary>
    private static string? Invalid(string name, string icon, IReadOnlyList<TemplateField> fields)
    {
        if (TemplateField.IsBlankOrLong(name, MaxNameLength))
        {
            return $"name is blank or longer than {MaxNameLength} characters.";
        }

        if (TemplateField.IsBlankOrLong(icon, MaxIconLength))
        {
            return $"icon is blank or longer than {MaxIconLength} characters.";
        }

        if (fields.Count == 0)
        {
            return "The template has no fields.";
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < fields.Count; i++)
        {
            if (fields[i] is not { } field)
            {
                return $"Entry {i} of fields is null.";
            }

            if (!keys.Add(field.Key))
            {
                return $"Entry {i} of fields has the key of an entry above it, without regard to case.";
            }
        }

        // levelByEnv reads the value of env, which is one of its options (design 6.2).
        var env = fields.FirstOrDefault(field => field.Key == TemplateField.Env);
        for (var i = 0; i < fields.Count; i++)
        {
            if (fields[i].LevelByEnv is not { } byEnv)
            {
                continue;
            }

            if (env is not { Type: FieldType.Select, Options: { } options })
            {
                return $"Entry {i} of fields has levelByEnv, but the template has no field env of type select.";
            }

            if (byEnv.Keys.Any(value => !options.Contains(value, StringComparer.Ordinal)))
            {
                return $"Entry {i} of fields has a key in levelByEnv that is not an option of env.";
            }
        }

        return null;
    }

    private JsonObject Content(string? stamp) => ModelJson.Write(
        new TemplateJson { Name = Name, Icon = Icon, Fields = [.. Fields.Select(field => field.ToJson())], Stamp = stamp },
        CoreJsonContext.Default.TemplateJson);
}

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Kiler.Core;

/// <summary>
/// The JSON of the models of Kiler.Core (M4), source-generated and case-sensitive, so a name in another case means nothing
/// to a model (storage.md, "Documents in the upper layers"). Every member of the JSON types is nullable, so a missing one is
/// a reason of the model, not an exception. A member that a model does not know is skipped when read and left out when
/// written.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LevelsJson))]
[JsonSerializable(typeof(RecordJson))]
[JsonSerializable(typeof(SettingsJson))]
[JsonSerializable(typeof(StashJson))]
[JsonSerializable(typeof(TemplateJson))]
[JsonSerializable(typeof(UserJson))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;

internal static class ModelJson
{
    /// <summary>The reason of a content whose value has another type, such as a text where a number belongs.</summary>
    public const string WrongType = "A value does not have the type it must have.";

    /// <summary>
    /// The content as its JSON type, or <c>null</c> when a value has another type. The message of the exception is not kept:
    /// its path can hold a name from the share.
    /// </summary>
    public static T? Read<T>(JsonObject content, JsonTypeInfo<T> type)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(content);
        try
        {
            return content.Deserialize(type);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonObject Write<T>(T value, JsonTypeInfo<T> type) => JsonSerializer.SerializeToNode(value, type)!.AsObject();
}

internal sealed class LevelsJson
{
    public List<LevelJson?>? Levels { get; init; }

    public KeysJson? Recovery { get; init; }
}

internal sealed class LevelJson
{
    public int? Level { get; init; }

    public string? Name { get; init; }

    public string? Check { get; init; }
}

/// <summary>A locked set of level keys (M3.3): the salt, and the keys under the digits of their levels.</summary>
internal sealed class KeysJson
{
    public string? Salt { get; init; }

    public Dictionary<string, string?>? Keys { get; init; }
}

/// <summary>A record of <c>records\</c> (design 5.2, M5.3); its dates and authors are those of <see cref="Authorship"/>.</summary>
internal sealed class RecordJson
{
    public string? Template { get; init; }

    public string? CreatedBy { get; init; }

    public string? CreatedAt { get; init; }

    public string? UpdatedBy { get; init; }

    public string? UpdatedAt { get; init; }

    public RecordPublicJson? Public { get; init; }

    public Dictionary<string, string?>? Protected { get; init; }

    public Dictionary<string, List<string?>?>? Fields { get; init; }
}

/// <summary>
/// The <c>public</c> of a record: the header at level 0 and, beside it, the fields of level 0, the description among them
/// (<see cref="Fields"/>), each a value of <see cref="FieldValue"/>.
/// </summary>
internal sealed class RecordPublicJson
{
    public List<string?>? Stash { get; init; }

    public string? Name { get; init; }

    public List<string?>? Tags { get; init; }

    /// <summary>The members that are not of the header, read as they are; <see cref="RecordFile.ToContent"/> writes them itself.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Fields { get; set; }
}

internal sealed class SettingsJson
{
    public bool? GuestMode { get; init; }

    public int? AutoLockMinutes { get; init; }
}

/// <summary>A stash of <c>stashes\</c> (design 6.6, M5.3); its dates and authors are those of <see cref="Authorship"/>.</summary>
internal sealed class StashJson
{
    public string? Name { get; init; }

    public string? CreatedBy { get; init; }

    public string? CreatedAt { get; init; }

    public string? UpdatedBy { get; init; }

    public string? UpdatedAt { get; init; }
}

internal sealed class TemplateJson
{
    public string? Name { get; init; }

    public string? Icon { get; init; }

    public List<TemplateFieldJson?>? Fields { get; init; }
}

/// <summary>A field of a template (design 6.2 to 6.5), its settings in the order of the examples of the design.</summary>
internal sealed class TemplateFieldJson
{
    public string? Key { get; init; }

    public string? Label { get; init; }

    public string? Type { get; init; }

    public List<string?>? Options { get; init; }

    public string? Language { get; init; }

    public string? Dialect { get; init; }

    public List<string?>? Languages { get; init; }

    public string? RefTemplate { get; init; }

    public int? Level { get; init; }

    public Dictionary<string, int?>? LevelByEnv { get; init; }

    public bool? Required { get; init; }

    public bool? Filter { get; init; }

    public bool? List { get; init; }
}

internal sealed class UserJson
{
    public string? DisplayName { get; init; }

    public int? Level { get; init; }

    public bool? CanEdit { get; init; }

    public bool? IsAdmin { get; init; }

    public bool? MustChangePassword { get; init; }

    public string? Salt { get; init; }

    public Dictionary<string, string?>? Keys { get; init; }
}

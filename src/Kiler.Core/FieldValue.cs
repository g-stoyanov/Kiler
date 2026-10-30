using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiler.Core;

/// <summary>The shape of a value of a field (docs/api.md, "Values by type").</summary>
public enum FieldValueKind
{
    /// <summary>A text: the value of most types, a date, an address and the id of a <c>ref</c> among them.</summary>
    Text,

    /// <summary>A number, of a <c>number</c>.</summary>
    Number,

    /// <summary>A code with the language chosen, of a <c>code</c> with <c>languages</c> (design 6.3).</summary>
    Code,
}

/// <summary>
/// A value of a field of a record (M5.3), one of the shapes of <see cref="FieldValueKind"/>. Whether it fits the type of its
/// field is the check of a write (M5.4): a read takes any shape, as a template can change after its records, and the edit
/// keeps a value that its template no longer allows (docs/api.md, <c>PUT /api/records/{id}</c>). A text is never empty, as
/// an empty text is no value.
/// </summary>
public sealed class FieldValue
{
    private FieldValue(FieldValueKind kind, string? text, double number, string? language)
    {
        Kind = kind;
        Text = text;
        Number = number;
        Language = language;
    }

    public FieldValueKind Kind { get; }

    /// <summary>The text of a <see cref="FieldValueKind.Text"/>, or the code of a <see cref="FieldValueKind.Code"/>; null for a number.</summary>
    public string? Text { get; }

    /// <summary>The number of a <see cref="FieldValueKind.Number"/>, finite; 0 for the others.</summary>
    public double Number { get; }

    /// <summary>The language of a <see cref="FieldValueKind.Code"/>; null for the others.</summary>
    public string? Language { get; }

    /// <exception cref="ArgumentException">The text is empty, which is no value.</exception>
    public static FieldValue OfText(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return new(FieldValueKind.Text, text, 0, null);
    }

    /// <exception cref="ArgumentOutOfRangeException">The number is not finite, which JSON cannot hold.</exception>
    public static FieldValue OfNumber(double number) =>
        double.IsFinite(number) ? new(FieldValueKind.Number, null, number, null) : throw new ArgumentOutOfRangeException(nameof(number), "The number is not finite.");

    public static FieldValue OfCode(string language, string code)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(code);
        return new(FieldValueKind.Code, code, 0, language);
    }

    /// <summary>
    /// The value of an element of a document or of a decrypted group, or <c>false</c> when it has no shape of a value. A
    /// <c>null</c> and an empty text are no value: <paramref name="value"/> is then <c>null</c>.
    /// </summary>
    internal static bool TryRead(JsonElement element, out FieldValue? value)
    {
        value = null;
        if (!Fits(element, out var isValue))
        {
            return false;
        }

        if (isValue)
        {
            value = element.ValueKind switch
            {
                JsonValueKind.String => OfText(element.GetString()!),
                JsonValueKind.Number => OfNumber(element.GetDouble()),
                _ => OfCode(element.GetProperty("language").GetString()!, element.GetProperty("code").GetString()!),
            };
        }

        return true;
    }

    /// <summary>
    /// Whether an element has the shape of a value, and whether it is one, without reading its text: so a <c>secret</c> of a
    /// decrypted group is checked and its value never becomes a string (M5.3).
    /// </summary>
    internal static bool Fits(JsonElement element, out bool isValue)
    {
        isValue = false;
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.String:
                isValue = !element.ValueEquals(ReadOnlySpan<byte>.Empty);
                return true;
            case JsonValueKind.Number:
                isValue = element.TryGetDouble(out var number) && double.IsFinite(number);
                return isValue;
            case JsonValueKind.Object:
                var members = 0;
                foreach (var member in element.EnumerateObject())
                {
                    if (++members > 2 || !(member.NameEquals("language"u8) || member.NameEquals("code"u8)) || member.Value.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }
                }

                isValue = members == 2 && element.TryGetProperty("language"u8, out _) && element.TryGetProperty("code"u8, out _);
                return isValue;
            default:
                return false;
        }
    }

    /// <summary>
    /// Whether an element of a decrypted group is a <c>secret</c> as Kiler writes it, <c>{ "secret": "&lt;value&gt;" }</c>
    /// (decided by the user in M5.3), and whether it has a value, without reading its text. The group is authenticated, so
    /// the mark holds also when the template on the share, which anyone can write, gives the field another type.
    /// </summary>
    internal static bool IsSecret(JsonElement element, out bool isValue)
    {
        isValue = false;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var members = 0;
        var value = default(JsonElement);
        foreach (var member in element.EnumerateObject())
        {
            if (++members > 1 || !member.NameEquals("secret"u8))
            {
                return false;
            }

            value = member.Value;
        }

        if (members != 1 || value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        isValue = !value.ValueEquals(ReadOnlySpan<byte>.Empty);
        return true;
    }

    /// <summary>The value in the JSON of a document or a group.</summary>
    internal JsonNode ToNode() => Kind switch
    {
        FieldValueKind.Text => JsonValue.Create(Text!),
        FieldValueKind.Number => JsonValue.Create(Number),
        _ => new JsonObject { ["language"] = Language, ["code"] = Text },
    };
}

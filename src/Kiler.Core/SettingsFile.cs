using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Kiler.Storage;

namespace Kiler.Core;

/// <summary>
/// <c>settings.json</c> (decided at the start of M4): guest mode, and the minutes without activity after which the session
/// locks. The wizard sets them; their screen comes after the exam. <c>id</c> and <c>version</c> are the document's.
/// </summary>
public sealed class SettingsFile
{
    /// <summary>The longest wait of the automatic locking: a day (decided by the user in M4.1).</summary>
    public const int MaxAutoLockMinutes = 1440;

    /// <exception cref="ArgumentException">The minutes are not from 1 to <see cref="MaxAutoLockMinutes"/>.</exception>
    public SettingsFile(bool guestMode, int autoLockMinutes)
    {
        if (Invalid(autoLockMinutes) is { } reason)
        {
            throw new ArgumentException(reason, nameof(autoLockMinutes));
        }

        GuestMode = guestMode;
        AutoLockMinutes = autoLockMinutes;
    }

    public static DocumentKey Key { get; } = DocumentKey.AtRoot("settings");

    /// <summary>Whether Kiler opens as a guest, who reads level 0, before a sign-in (design 7.5).</summary>
    public bool GuestMode { get; }

    public int AutoLockMinutes { get; }

    /// <summary>The model of the document, or why its content does not fit.</summary>
    /// <exception cref="ArgumentException">The document is not <see cref="Key"/>.</exception>
    public static bool TryRead(Document document, [NotNullWhen(true)] out SettingsFile? settings, [NotNullWhen(false)] out string? invalid)
    {
        ArgumentNullException.ThrowIfNull(document);
        return TryRead(document.Key, document.Content, out settings, out invalid);
    }

    /// <summary>The content of the document, without <c>id</c> and <c>version</c>, which the layer adds.</summary>
    public JsonObject ToContent() => ModelJson.Write(
        new SettingsJson { GuestMode = GuestMode, AutoLockMinutes = AutoLockMinutes },
        CoreJsonContext.Default.SettingsJson);

    internal static bool TryRead(DocumentKey key, JsonObject content, [NotNullWhen(true)] out SettingsFile? settings, [NotNullWhen(false)] out string? invalid)
    {
        if (key != Key)
        {
            throw new ArgumentException("The document is not settings.json.", nameof(key));
        }

        settings = null;
        if (ModelJson.Read(content, CoreJsonContext.Default.SettingsJson) is not { } json)
        {
            invalid = ModelJson.WrongType;
            return false;
        }

        if (json.GuestMode is not { } guestMode)
        {
            invalid = "guestMode is missing.";
            return false;
        }

        if (json.AutoLockMinutes is not { } autoLockMinutes)
        {
            invalid = "autoLockMinutes is missing.";
            return false;
        }

        invalid = Invalid(autoLockMinutes);
        if (invalid is not null)
        {
            return false;
        }

        settings = new SettingsFile(guestMode, autoLockMinutes);
        return true;
    }

    private static string? Invalid(int autoLockMinutes) =>
        autoLockMinutes is < 1 or > MaxAutoLockMinutes ? $"autoLockMinutes is not from 1 to {MaxAutoLockMinutes}." : null;
}

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Kiler.Crypto;
using Kiler.Storage;

namespace Kiler.Core;

/// <summary>
/// <c>settings.json</c> (decided at the start of M4): guest mode, and the minutes without activity after which the session
/// locks. The wizard sets them; their screen comes after the exam. <c>id</c> and <c>version</c> are the document's. Its
/// <c>stamp</c> is the admin seal of the whole file, with its id and version (M4.11, <see cref="IsSealedBy"/>).
/// </summary>
public sealed class SettingsFile
{
    /// <summary>The longest wait of the automatic locking: a day (decided by the user in M4.1).</summary>
    public const int MaxAutoLockMinutes = 1440;

    private readonly FileSeal? _seal;

    /// <exception cref="ArgumentException">The minutes are not from 1 to <see cref="MaxAutoLockMinutes"/>.</exception>
    public SettingsFile(bool guestMode, int autoLockMinutes)
        : this(guestMode, autoLockMinutes, seal: null)
    {
    }

    private SettingsFile(bool guestMode, int autoLockMinutes, FileSeal? seal)
    {
        if (Invalid(autoLockMinutes) is { } reason)
        {
            throw new ArgumentException(reason, nameof(autoLockMinutes));
        }

        GuestMode = guestMode;
        AutoLockMinutes = autoLockMinutes;
        _seal = seal;
    }

    public static DocumentKey Key { get; } = DocumentKey.AtRoot("settings");

    /// <summary>Whether Kiler opens as a guest, who reads level 0, before a sign-in (design 7.5).</summary>
    public bool GuestMode { get; }

    public int AutoLockMinutes { get; }

    /// <summary>The <c>stamp</c> of the file, or <c>null</c>; it may not be a seal at all (<see cref="IsSealedBy"/>).</summary>
    public string? Stamp => _seal?.Stamp;

    /// <summary>The model of the document, or why its content does not fit.</summary>
    /// <exception cref="ArgumentException">The document is not <see cref="Key"/>.</exception>
    public static bool TryRead(Document document, [NotNullWhen(true)] out SettingsFile? settings, [NotNullWhen(false)] out string? invalid)
    {
        ArgumentNullException.ThrowIfNull(document);
        return TryRead(document.Key, document.Version, document.Content, out settings, out invalid);
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
    public SettingsFile Sealed(AdminKey adminKey, int version)
    {
        ArgumentNullException.ThrowIfNull(adminKey);
        return new(GuestMode, AutoLockMinutes, FileSeal.Make(adminKey, SealPurpose.Settings, Key, version, Content(stamp: null)));
    }

    /// <summary>The content of the document, without <c>id</c> and <c>version</c>, which the layer adds, and with its <c>stamp</c>.</summary>
    public JsonObject ToContent() => Content(Stamp);

    internal static bool TryRead(DocumentKey key, int version, JsonObject content, [NotNullWhen(true)] out SettingsFile? settings, [NotNullWhen(false)] out string? invalid)
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

        settings = new SettingsFile(guestMode, autoLockMinutes, FileSeal.Read(SealPurpose.Settings, key, version, content, json.Stamp));
        return true;
    }

    /// <summary>Why the minutes do not fit, or <c>null</c>; also the check of the wizard (<see cref="NewKiler"/>).</summary>
    internal static string? Invalid(int autoLockMinutes) =>
        autoLockMinutes is < 1 or > MaxAutoLockMinutes ? $"autoLockMinutes is not from 1 to {MaxAutoLockMinutes}." : null;

    private JsonObject Content(string? stamp) => ModelJson.Write(
        new SettingsJson { GuestMode = GuestMode, AutoLockMinutes = AutoLockMinutes, Stamp = stamp },
        CoreJsonContext.Default.SettingsJson);
}

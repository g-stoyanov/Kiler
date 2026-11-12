using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Kiler.Crypto;
using Kiler.Storage;

namespace Kiler.Core;

/// <summary>
/// <c>users\&lt;account&gt;.json</c> (decided at the start of M4, design 7.3): the user's display name, level and rights, and
/// the level keys locked with their password. The account is the id of the document, and <c>version</c> is the document's.
/// <see cref="Level"/>, <see cref="CanEdit"/> and <see cref="IsAdmin"/> say what the screens show; what a user reads is the
/// keys that unlock and verify, never <see cref="Level"/> (design 7.8). It is on the share, so a content that does not fit
/// is invalid, with a reason (<see cref="TryRead(Document, out UserFile?, out string?)"/>).
/// <para>
/// Its <c>stamp</c> is the admin seal of its rights (M4.11, <see cref="IsSealedBy"/>): the account, the display name, the
/// level, <c>canEdit</c> and <c>isAdmin</c>, never the keys, <c>mustChangePassword</c> or the version, which the user
/// changes with their password (<see cref="WithKeys"/>).
/// </para>
/// </summary>
public sealed class UserFile
{
    public const int MaxDisplayNameLength = 100;

    /// <summary>
    /// A user file of <paramref name="account"/>, which must pass <see cref="StorageName"/>, with <paramref name="keys"/> of
    /// the levels 1 to 9 locked with the user's password (<see cref="LevelKeys"/>), not sealed (<see cref="Sealed"/>).
    /// </summary>
    /// <exception cref="ArgumentException">A value does not fit; the message says why.</exception>
    public UserFile(string account, string displayName, int level, bool canEdit, bool isAdmin, bool mustChangePassword, LockedKeys keys)
        : this(account, displayName, level, canEdit, isAdmin, mustChangePassword, keys, stamp: null)
    {
    }

    private UserFile(string account, string displayName, int level, bool canEdit, bool isAdmin, bool mustChangePassword, LockedKeys keys, string? stamp)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(keys);
        StorageName.Check(account);
        if ((Invalid(displayName, level) ?? KeySets.Invalid(keys, LevelsFile.MaxTop, "")) is { } reason)
        {
            throw new ArgumentException(reason);
        }

        Account = account;
        DisplayName = displayName;
        Level = level;
        CanEdit = canEdit;
        IsAdmin = isAdmin;
        MustChangePassword = mustChangePassword;
        Keys = KeySets.Copy(keys);
        Stamp = stamp;
    }

    public string Account { get; }

    public DocumentKey Key => KeyOf(Account);

    public string DisplayName { get; }

    /// <summary>The level that the screens show, from 0 to <see cref="LevelsFile.MaxTop"/>.</summary>
    public int Level { get; }

    public bool CanEdit { get; }

    public bool IsAdmin { get; }

    /// <summary>Whether the password is a temporary one, which the user changes before anything else (design 7.6).</summary>
    public bool MustChangePassword { get; }

    /// <summary>
    /// The <c>salt</c>, the <c>keys</c> and the locked admin key of the file, for
    /// <see cref="LevelKeys.UnlockAll(LockedKeys, ReadOnlySpan{char})"/>.
    /// </summary>
    public LockedKeys Keys { get; }

    /// <summary>The <c>stamp</c> of the file, or <c>null</c>; it may not be a seal at all (<see cref="IsSealedBy"/>).</summary>
    public string? Stamp { get; }

    /// <summary>The user file of <paramref name="account"/>, which must pass <see cref="StorageName"/>.</summary>
    public static DocumentKey KeyOf(string account) => DocumentKey.InFolder(DataFolder.Users, account);

    /// <summary>
    /// Whether <see cref="Stamp"/> is the seal of the rights of the file by the admin key of <paramref name="publicKey"/>, the
    /// base64 of its SubjectPublicKeyInfo; a file without one, or with one that is not such a seal, is not sealed.
    /// </summary>
    public bool IsSealedBy(string publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        return Stamp is { } stamp && AdminSeal.Verify(publicKey, SealPurpose.UserFile, SealText(), stamp);
    }

    /// <summary>The file with the seal of its rights by <paramref name="adminKey"/> as its <c>stamp</c>.</summary>
    /// <exception cref="ObjectDisposedException">The admin key is disposed.</exception>
    public UserFile Sealed(AdminKey adminKey)
    {
        ArgumentNullException.ThrowIfNull(adminKey);
        return new(Account, DisplayName, Level, CanEdit, IsAdmin, MustChangePassword, Keys, adminKey.Seal(SealPurpose.UserFile, SealText()));
    }

    /// <summary>
    /// The file with other keys, locked with a new password, and <paramref name="mustChangePassword"/>, its rights and its
    /// <c>stamp</c> as they are, as the seal does not cover them (a change of password).
    /// </summary>
    public UserFile WithKeys(LockedKeys keys, bool mustChangePassword)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return new(Account, DisplayName, Level, CanEdit, IsAdmin, mustChangePassword, keys, Stamp);
    }

    /// <summary>The model of the document, or why its content does not fit.</summary>
    /// <exception cref="ArgumentException">The document is not in <c>users\</c> of the data folder.</exception>
    public static bool TryRead(Document document, [NotNullWhen(true)] out UserFile? user, [NotNullWhen(false)] out string? invalid)
    {
        ArgumentNullException.ThrowIfNull(document);
        return TryRead(document.Key, document.Content, out user, out invalid);
    }

    /// <summary>The content of the document, without <c>id</c> and <c>version</c>, which the layer adds.</summary>
    public JsonObject ToContent()
    {
        var keys = KeySets.Write(Keys);
        return ModelJson.Write(
            new UserJson
            {
                DisplayName = DisplayName,
                Level = Level,
                CanEdit = CanEdit,
                IsAdmin = IsAdmin,
                MustChangePassword = MustChangePassword,
                Salt = keys.Salt,
                Keys = keys.Keys,
                AdminPublicKey = keys.AdminPublicKey,
                AdminPrivateKey = keys.AdminPrivateKey,
                Stamp = Stamp,
            },
            CoreJsonContext.Default.UserJson);
    }

    internal static bool TryRead(DocumentKey key, JsonObject content, [NotNullWhen(true)] out UserFile? user, [NotNullWhen(false)] out string? invalid)
    {
        if (key.Account is not null || key.Folder != DataFolder.Users)
        {
            throw new ArgumentException("The document is not a user file.", nameof(key));
        }

        user = null;
        if (ModelJson.Read(content, CoreJsonContext.Default.UserJson) is not { } json)
        {
            invalid = ModelJson.WrongType;
            return false;
        }

        if (json.DisplayName is not { } displayName)
        {
            invalid = "displayName is missing.";
            return false;
        }

        if (json.Level is not { } level)
        {
            invalid = "level is missing.";
            return false;
        }

        if (json.CanEdit is not { } canEdit)
        {
            invalid = "canEdit is missing.";
            return false;
        }

        if (json.IsAdmin is not { } isAdmin)
        {
            invalid = "isAdmin is missing.";
            return false;
        }

        if (json.MustChangePassword is not { } mustChangePassword)
        {
            invalid = "mustChangePassword is missing.";
            return false;
        }

        invalid = Invalid(displayName, level);
        if (invalid is not null)
        {
            return false;
        }

        if (!KeySets.TryRead(json.Salt, json.Keys, json.AdminPublicKey, json.AdminPrivateKey, LevelsFile.MaxTop, "", out var keys, out invalid))
        {
            return false;
        }

        user = new UserFile(key.Id, displayName, level, canEdit, isAdmin, mustChangePassword, keys, json.Stamp);
        return true;
    }

    /// <summary>Why the display name or the level do not fit, or <c>null</c>; also the check of the wizard (<see cref="NewKiler"/>).</summary>
    internal static string? Invalid(string displayName, int level)
    {
        if (displayName.Length > MaxDisplayNameLength || string.IsNullOrWhiteSpace(displayName))
        {
            return $"displayName is blank or longer than {MaxDisplayNameLength} characters.";
        }

        return level is < 0 or > LevelsFile.MaxTop ? $"level is not from 0 to {LevelsFile.MaxTop}." : null;
    }

    private byte[] SealText() => FileSeal.TextOf(Account, DisplayName, Level, CanEdit, IsAdmin);
}

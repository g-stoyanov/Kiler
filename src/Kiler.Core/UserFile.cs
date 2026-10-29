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
/// </summary>
public sealed class UserFile
{
    public const int MaxDisplayNameLength = 100;

    /// <summary>
    /// A user file of <paramref name="account"/>, which must pass <see cref="StorageName"/>, with <paramref name="keys"/> of
    /// the levels 1 to 9 locked with the user's password (<see cref="LevelKeys"/>).
    /// </summary>
    /// <exception cref="ArgumentException">A value does not fit; the message says why.</exception>
    public UserFile(string account, string displayName, int level, bool canEdit, bool isAdmin, bool mustChangePassword, LockedKeys keys)
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

    /// <summary>The <c>salt</c> and the <c>keys</c> of the file, for <see cref="LevelKeys.Unlock(LockedKeys, ReadOnlySpan{char})"/>.</summary>
    public LockedKeys Keys { get; }

    /// <summary>The user file of <paramref name="account"/>, which must pass <see cref="StorageName"/>.</summary>
    public static DocumentKey KeyOf(string account) => DocumentKey.InFolder(DataFolder.Users, account);

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

        if (!KeySets.TryRead(json.Salt, json.Keys, LevelsFile.MaxTop, "", out var keys, out invalid))
        {
            return false;
        }

        user = new UserFile(key.Id, displayName, level, canEdit, isAdmin, mustChangePassword, keys);
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
}

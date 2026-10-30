using System.Diagnostics.CodeAnalysis;
using Kiler.Storage;

namespace Kiler.Core;

/// <summary>
/// The admin actions (M4.6, design 7.6, step 7 of M4 without the seal): a new user, a new temporary password with a level (a
/// change of level, or a forgotten password), the editor and admin rights, and the rename of a level. An action that gives
/// keys gives those of a level that the admin chose, never of the level of a user file, and locks the admin's keys of the
/// levels 1 to the user's level with a temporary password that Kiler makes (<see cref="Password.NewTemporary"/>), sets
/// <c>mustChangePassword</c>, and gives the password once, in its result. Each writes through the team, over the version that
/// the team has (<see cref="Team.Create"/>, <see cref="Team.Replace(UserFile, int)"/>, <see cref="Team.Replace(LevelsFile, int)"/>).
/// The caller has checked <see cref="Need.Admin"/>; an action checks again that the admin holds every level of
/// <c>levels.json</c> as the team has it now (<see cref="Session.HoldsEvery"/>), as it may have changed since. Decided by the
/// user in M4.6: no action changes the admin's own user file, an admin's level is not lowered below the top level, and a user
/// has a level from 1, as a file without keys cannot sign in (M4.4). A change that the admin makes reaches a signed-in user at
/// their next sign-in, and makes their change of password ask for one (M4.5a).
/// </summary>
public static class AdminActions
{
    public const string AccountInvalid =
        "account is not a name of 1 to 64 letters a to z, digits, '.', '-' and '_', with no '.' at its start or end, nor a name of a device.";

    /// <summary>
    /// Whether the keys of <paramref name="user"/> are those of the levels 1 to its level, no more and no fewer: the list of
    /// users marks a file with a level without its key, or a key above it (M4.6). It sees the names of the keys only: whether a
    /// key is the key of its level, only the user's password can tell.
    /// </summary>
    public static bool KeysMatchLevel(UserFile user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Keys.Keys.Keys.Order().SequenceEqual(Enumerable.Range(1, user.Level));
    }

    /// <summary>Whether the team has fewer than two admins, user files that fit with <c>isAdmin</c>: the list warns then (design 7.7).</summary>
    public static bool FewAdmins(TeamFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files.Users.Values.Count(file => file.IsValid && file.Model.IsAdmin) < 2;
    }

    /// <summary>
    /// A new user (design 7.6): the user file of <paramref name="account"/> with the admin's keys of the levels 1 to
    /// <paramref name="level"/>, locked with a new temporary password. The account is a Windows user name, made the account as
    /// a Kiler makes its own (<see cref="AccountName.From"/>). An account with a user file, or one that had one, is
    /// <see cref="AdminOutcome.Exists"/>. PBKDF2 runs once.
    /// </summary>
    /// <exception cref="SessionLockedException">The session was locked before or while its keys were locked.</exception>
    public static AdminResult NewUser(Session admin, Team team, string? account, string? displayName, int level, bool canEdit)
    {
        ArgumentNullException.ThrowIfNull(team);
        var files = team.Files;
        if (!TryLevels(admin, files, out var levels, out _, out var refusal))
        {
            return new(refusal);
        }

        if (account is null || displayName is null)
        {
            return new(AdminOutcome.Invalid, account is null ? "account is missing." : "displayName is missing.");
        }

        if (AccountName.From(account) is not { } name)
        {
            return new(AdminOutcome.Invalid, AccountInvalid);
        }

        if ((LevelInvalid(level, levels) ?? UserFile.Invalid(displayName, level)) is { } invalid)
        {
            return new(AdminOutcome.Invalid, invalid);
        }

        if (files.Users.ContainsKey(name))
        {
            return new(AdminOutcome.Exists);
        }

        var temporary = Password.NewTemporary();
        var user = new UserFile(name, displayName, level, canEdit, isAdmin: false, mustChangePassword: true, admin.LockKeys(level, temporary));
        return Write(() => team.Create(user), null, new(AdminOutcome.Done, User: user, TemporaryPassword: temporary));
    }

    /// <summary>
    /// A new temporary password with the level that the admin gives (design 7.6), for a change of level and for a forgotten
    /// password alike: the admin's keys of the levels 1 to <paramref name="level"/> only, locked with it. Never the level of
    /// the user file, which its user can write: a user who wrote a higher level into their own file would get its keys from
    /// the admin (security review, decided by the user in M4.6). An admin's level is not lowered below the top level
    /// (<see cref="AdminOutcome.AdminLevel"/>).
    /// </summary>
    /// <exception cref="SessionLockedException">The session was locked before or while its keys were locked.</exception>
    public static AdminResult ChangeLevel(Session admin, Team team, string account, int level)
    {
        ArgumentNullException.ThrowIfNull(team);
        var files = team.Files;
        if (!TryLevels(admin, files, out var levels, out _, out var refusal) || !TryUser(admin, files, account, out var user, out var version, out refusal))
        {
            return new(refusal);
        }

        if (LevelInvalid(level, levels) is { } invalid)
        {
            return new(AdminOutcome.Invalid, invalid);
        }

        if (user.IsAdmin && level < levels.Top)
        {
            return new(AdminOutcome.AdminLevel);
        }

        var temporary = Password.NewTemporary();
        var changed = new UserFile(user.Account, user.DisplayName, level, user.CanEdit, user.IsAdmin, mustChangePassword: true, admin.LockKeys(level, temporary));
        return Write(() => team.Replace(changed, version), version, new(AdminOutcome.Done, User: changed, TemporaryPassword: temporary));
    }

    /// <summary>
    /// The editor and admin rights. Making someone an admin gives them the top level, with the admin's keys of every level
    /// locked with a new temporary password, as an admin holds them all (decided at the start of M4); the other changes keep
    /// the keys, the level and the password. Rights that are as they are already write nothing.
    /// </summary>
    /// <exception cref="SessionLockedException">The session was locked before or while its keys were locked.</exception>
    public static AdminResult SetRights(Session admin, Team team, string account, bool canEdit, bool isAdmin)
    {
        ArgumentNullException.ThrowIfNull(team);
        var files = team.Files;
        if (!TryLevels(admin, files, out var levels, out _, out var refusal) || !TryUser(admin, files, account, out var user, out var version, out refusal))
        {
            return new(refusal);
        }

        if (isAdmin && !user.IsAdmin)
        {
            var temporary = Password.NewTemporary();
            var raised = new UserFile(user.Account, user.DisplayName, levels.Top, canEdit, isAdmin: true, mustChangePassword: true, admin.LockKeys(levels.Top, temporary));
            return Write(() => team.Replace(raised, version), version, new(AdminOutcome.Done, User: raised, TemporaryPassword: temporary));
        }

        if (canEdit == user.CanEdit && isAdmin == user.IsAdmin)
        {
            return new(AdminOutcome.Done, User: user);
        }

        var changed = new UserFile(user.Account, user.DisplayName, user.Level, canEdit, isAdmin, user.MustChangePassword, user.Keys);
        return Write(() => team.Replace(changed, version), version, new(AdminOutcome.Done, User: changed));
    }

    /// <summary>
    /// Renames <paramref name="level"/> in <c>levels.json</c> (decided at the start of M4: the levels screen only renames). The
    /// checks and the recovery set stay as they are. A name as it is already writes nothing.
    /// </summary>
    /// <exception cref="SessionLockedException">The session was locked before it.</exception>
    public static AdminResult RenameLevel(Session admin, Team team, int level, string? name)
    {
        ArgumentNullException.ThrowIfNull(team);
        if (!TryLevels(admin, team.Files, out var levels, out var version, out var refusal))
        {
            return new(refusal);
        }

        if (level < 0 || level > levels.Top)
        {
            return new(AdminOutcome.Invalid, $"level is not from 0 to {levels.Top}.");
        }

        if (name is null)
        {
            return new(AdminOutcome.Invalid, "name is missing.");
        }

        var entries = levels.Levels.Select(entry => entry.Level == level ? entry with { Name = name } : entry).ToList();
        if (LevelsFile.Invalid(entries) is { } invalid)
        {
            return new(AdminOutcome.Invalid, invalid);
        }

        if (string.Equals(name, levels.Levels[level].Name, StringComparison.Ordinal))
        {
            return new(AdminOutcome.Done, Levels: levels);
        }

        var renamed = new LevelsFile(entries, levels.Recovery);
        return Write(() => team.Replace(renamed, version), version, new(AdminOutcome.Done, Levels: renamed));
    }

    /// <summary>
    /// <c>levels.json</c> of <paramref name="files"/> and its version, when the admin holds every level of it, verified against
    /// its checks now; else <see cref="AdminOutcome.LevelsChanged"/>, and the admin signs in again.
    /// </summary>
    /// <exception cref="SessionLockedException">The session is locked.</exception>
    private static bool TryLevels(Session admin, TeamFiles files, [NotNullWhen(true)] out LevelsFile? levels, out int version, out AdminOutcome refusal)
    {
        ArgumentNullException.ThrowIfNull(admin);

        // An action that writes no key would not meet the lock otherwise.
        if (admin.IsLocked)
        {
            throw new SessionLockedException();
        }

        levels = files.Levels?.Model;

        // A file that fits its model is not damaged, so it has a version.
        version = files.Levels?.Version ?? 0;
        if (levels is null || !admin.HoldsEvery(levels))
        {
            levels = null;
            refusal = AdminOutcome.LevelsChanged;
            return false;
        }

        refusal = default;
        return true;
    }

    /// <summary>The user file of <paramref name="account"/> that fits its model, and its version; else why the action cannot change it.</summary>
    private static bool TryUser(Session admin, TeamFiles files, string account, [NotNullWhen(true)] out UserFile? user, out int version, out AdminOutcome refusal)
    {
        ArgumentNullException.ThrowIfNull(account);
        user = null;
        version = 0;
        if (string.Equals(account, admin.Account, StringComparison.Ordinal))
        {
            refusal = AdminOutcome.OwnAccount;
            return false;
        }

        if (!files.Users.TryGetValue(account, out var file))
        {
            refusal = AdminOutcome.NoUser;
            return false;
        }

        if (!file.IsValid)
        {
            refusal = AdminOutcome.Unreadable;
            return false;
        }

        user = file.Model;
        version = file.Version!.Value;
        refusal = default;
        return true;
    }

    /// <summary>Why <paramref name="level"/> cannot be the level of a user: level 0 has no key, so its file could not sign in.</summary>
    private static string? LevelInvalid(int level, LevelsFile levels) =>
        level >= 1 && level <= levels.Top ? null : $"level is not from 1 to {levels.Top}.";

    private static AdminResult Write(Func<WriteResult> write, int? version, AdminResult done)
    {
        WriteResult written;
        try
        {
            written = write();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A share that cannot be reached before a load has said so (core.md, "Calling the layer").
            return new(AdminOutcome.NotWritten);
        }

        return written.Outcome switch
        {
            WriteOutcome.Written => done,
            WriteOutcome.Exists => new(AdminOutcome.Exists),
            WriteOutcome.Offline => new(AdminOutcome.Offline),
            WriteOutcome.Busy => new(AdminOutcome.Busy),

            // The slot of the version is in the way: another write of it is under way, or a crash left it (core.md).
            WriteOutcome.Conflict when version is not null && written.OnDisk?.Version == version => new(AdminOutcome.Busy),

            // Another version, or the file has gone or is damaged: the team has not followed the share yet.
            WriteOutcome.Conflict or WriteOutcome.Missing or WriteOutcome.Damaged => new(AdminOutcome.Changed),
            _ => new(AdminOutcome.NotWritten),
        };
    }
}

/// <summary>How an admin action ended (<see cref="AdminActions"/>).</summary>
public enum AdminOutcome
{
    /// <summary>The file is written, or was as the action would make it.</summary>
    Done,

    /// <summary>A value of the request does not fit; <see cref="AdminResult.Invalid"/> says why.</summary>
    Invalid,

    /// <summary>The account is the admin's own: another admin changes it (decided by the user in M4.6).</summary>
    OwnAccount,

    /// <summary>The account has no user file.</summary>
    NoUser,

    /// <summary>The user file is damaged or does not fit its model, so the action cannot keep what it holds.</summary>
    Unreadable,

    /// <summary>A new user whose account has a user file, or had one: its history is there (M2.5).</summary>
    Exists,

    /// <summary>A change of an admin's level below the top level: the admin right goes first (decided by the user in M4.6).</summary>
    AdminLevel,

    /// <summary><c>levels.json</c> is missing, does not fit, or has other checks than the keys of the admin verified against: the admin signs in again.</summary>
    LevelsChanged,

    /// <summary>The file has changed on the share, and the team has not followed it yet: the page reads it again.</summary>
    Changed,

    /// <summary>Another write of the file is under way, or a file of it is held: the action can be tried again in a moment.</summary>
    Busy,

    /// <summary>The shared folder cannot be reached (<see cref="WriteOutcome.Offline"/>): the action can be made once it is back.</summary>
    Offline,

    /// <summary>The file could not be written: the share did not answer or refused it, or its version has no next one.</summary>
    NotWritten,
}

/// <param name="Outcome">How the action ended.</param>
/// <param name="Invalid">Why a value does not fit, for the page, with <see cref="AdminOutcome.Invalid"/>; else null.</param>
/// <param name="User">The user file as the action left it; null for the rename of a level and for a refusal.</param>
/// <param name="TemporaryPassword">
/// The temporary password that the action made, for its answer only, once (kiler-crypto, "Passwords"); null when it gave none.
/// </param>
/// <param name="Levels"><c>levels.json</c> as the rename of a level left it; null for the other actions and for a refusal.</param>
public sealed record AdminResult(AdminOutcome Outcome, string? Invalid = null, UserFile? User = null, string? TemporaryPassword = null, LevelsFile? Levels = null)
{
    /// <summary>The name of the type: a record prints its properties, and this one holds a password (SKILL.md, "Style").</summary>
    public override string ToString() => nameof(AdminResult);
}

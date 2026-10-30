using Kiler.Crypto;
using Kiler.Storage;

namespace Kiler.Core;

/// <summary>
/// The change of password (M4.5a, design 7.6, decided at the start of M4): the keys of the session locked again with the new
/// password, into the user file of its account, which loses <c>mustChangePassword</c>, so the session gets its rights. The
/// forced change after a temporary password uses the keys of the sign-in; a voluntary one asks for the current password. The
/// keys stay the same, so what they locked before reads after it.
/// </summary>
public static class PasswordChange
{
    /// <summary>
    /// Changes the password of <paramref name="session"/> through <paramref name="team"/> (<see cref="Team.Replace(UserFile, int)"/>), and,
    /// when the user file is written, clears the mark of a temporary password in the session. It refuses a user file at another
    /// version than the session knows, as the admin may have changed its level or given a new temporary password meanwhile
    /// (decided by the user in M4.5), and one with a level whose key the session does not hold, as the level did not unlock or
    /// verify at the sign-in and would drop out of the file for good (M3.3). PBKDF2 runs once, and once more for the current
    /// password of a voluntary change.
    /// </summary>
    /// <param name="session">The session of the request, which the rights let through (<see cref="Need.SignedIn"/>).</param>
    /// <param name="team">The team of the local copy, which writes the user file.</param>
    /// <param name="current">
    /// The current password of a voluntary change, which has passed <see cref="Password.Invalid"/>; a session with a temporary
    /// password needs none, and a voluntary change without it is <see cref="PasswordChangeOutcome.WrongPassword"/>.
    /// </param>
    /// <param name="password">The new password, which has passed <see cref="Password.Invalid"/>.</param>
    /// <exception cref="ArgumentException">A password does not fit (<see cref="Password.Invalid"/>), a fault of the caller.</exception>
    /// <exception cref="SessionLockedException">The session was locked before or while its keys were locked.</exception>
    public static PasswordChangeOutcome Run(Session session, Team team, string? current, ReadOnlySpan<char> password)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(team);
        if ((Password.Invalid(password) ?? (current is null ? null : Password.Invalid(current))) is { } invalid)
        {
            throw new ArgumentException(invalid);
        }

        // Read once, so the checks and the write are of one moment.
        var version = session.UserVersion;
        var forced = session.MustChangePassword;
        if (!team.Files.Users.TryGetValue(session.Account, out var file) || !file.IsValid || file.Version != version)
        {
            return PasswordChangeOutcome.UserChanged;
        }

        var user = file.Model;
        if (!user.Keys.Keys.Keys.All(session.Reads))
        {
            return PasswordChangeOutcome.LevelNotUnlocked;
        }

        if (!forced && (current is null || !Unlocks(user.Keys, current)))
        {
            return PasswordChangeOutcome.WrongPassword;
        }

        var changed = new UserFile(user.Account, user.DisplayName, user.Level, user.CanEdit, user.IsAdmin, mustChangePassword: false, session.LockKeys(password));
        WriteResult written;
        try
        {
            written = team.Replace(changed, version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A share that cannot be reached before a load has said so (core.md, "Calling the layer").
            return PasswordChangeOutcome.NotWritten;
        }

        if (written.Outcome == WriteOutcome.Written)
        {
            session.PasswordChanged(written.Version);
        }

        return written.Outcome switch
        {
            WriteOutcome.Written => PasswordChangeOutcome.Changed,
            WriteOutcome.Offline => PasswordChangeOutcome.Offline,
            WriteOutcome.Busy => PasswordChangeOutcome.Busy,

            // The slot of the version is in the way: another write of it is under way, or a crash left it (core.md).
            WriteOutcome.Conflict when written.OnDisk?.Version == version => PasswordChangeOutcome.Busy,

            // Another version, or the file has gone or is damaged: the team has not followed the share yet.
            WriteOutcome.Conflict or WriteOutcome.Missing or WriteOutcome.Damaged => PasswordChangeOutcome.UserChanged,
            _ => PasswordChangeOutcome.NotWritten,
        };
    }

    /// <summary>Whether <paramref name="password"/> unlocks a key of <paramref name="keys"/>; the keys are disposed at once.</summary>
    private static bool Unlocks(LockedKeys keys, string password)
    {
        var unlocked = LevelKeys.Unlock(keys, password);
        foreach (var key in unlocked.Values)
        {
            key.Dispose();
        }

        return unlocked.Count > 0;
    }
}

/// <summary>How a change of password ended (<see cref="PasswordChange.Run"/>).</summary>
public enum PasswordChangeOutcome
{
    /// <summary>The user file has the keys locked with the new password, and the session has its rights.</summary>
    Changed,

    /// <summary>A voluntary change, and the current password is missing or unlocks no key of the user file.</summary>
    WrongPassword,

    /// <summary>A level of the user file did not unlock or verify at the sign-in, so the session does not hold its key (M3.3).</summary>
    LevelNotUnlocked,

    /// <summary>
    /// The user file is missing, does not fit, or is at another version than the session knows, here or on the share: it has
    /// changed since the sign-in, so the user signs in again (decided by the user in M4.5).
    /// </summary>
    UserChanged,

    /// <summary>Another write of the user file is under way, or a file of it is held: the change can be tried again in a moment.</summary>
    Busy,

    /// <summary>The shared folder cannot be reached (<see cref="WriteOutcome.Offline"/>): the change can be made once it is back.</summary>
    Offline,

    /// <summary>The user file could not be written: the share did not answer or refused it, or its version has no next one.</summary>
    NotWritten,
}

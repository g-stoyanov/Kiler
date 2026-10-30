using System.Diagnostics.CodeAnalysis;
using Kiler.Crypto;

namespace Kiler.Core;

/// <summary>
/// The sign-in (M4.4, design 7.5): the user file of the account, its keys unlocked with the password, and of them only the
/// ones that pass the check of their level in <c>levels.json</c> (kiler-crypto, "Rules"). With none, there is no access. The
/// files are those of the team at one moment (<see cref="Team.Files"/>), read once.
/// </summary>
public static class SignIn
{
    /// <summary>
    /// A session of <paramref name="account"/>, or why there is none. <paramref name="password"/> has passed
    /// <see cref="Password.Invalid"/>; PBKDF2 runs once, so it takes a moment. The keys that do not verify are disposed.
    /// </summary>
    /// <exception cref="ArgumentException">The password does not fit (<see cref="Password.Invalid"/>), a fault of the caller.</exception>
    public static bool TryStart(TeamFiles files, string account, ReadOnlySpan<char> password, [NotNullWhen(true)] out Session? session, out SignInRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(account);
        if (Password.Invalid(password) is { } invalid)
        {
            throw new ArgumentException(invalid, nameof(password));
        }

        session = null;
        if (!files.Users.TryGetValue(account, out var file))
        {
            refusal = SignInRefusal.NoUser;
            return false;
        }

        if (!file.IsValid)
        {
            refusal = SignInRefusal.UserInvalid;
            return false;
        }

        if (files.Levels is not { IsValid: true } levelsFile)
        {
            refusal = SignInRefusal.LevelsInvalid;
            return false;
        }

        var user = file.Model;
        if (user.Keys.Keys.Count == 0)
        {
            refusal = SignInRefusal.NoAccess;
            return false;
        }

        var unlocked = LevelKeys.Unlock(user.Keys, password);
        if (unlocked.Count == 0)
        {
            refusal = SignInRefusal.WrongPassword;
            return false;
        }

        var verified = Verified(unlocked, levelsFile.Model);
        if (verified.Count == 0)
        {
            refusal = SignInRefusal.NoAccess;
            return false;
        }

        // A file that fits its model is not damaged, so it has a version.
        session = new Session(user, file.Version!.Value, verified);
        refusal = default;
        return true;
    }

    /// <summary>
    /// The keys that pass the check of their level, which the caller then owns; the rest are disposed. A level above the top
    /// level, or one without a check, does not verify (M3.2): the check is never given to <see cref="LevelCheck.Verify"/> as null.
    /// </summary>
    private static Dictionary<int, SecretKey> Verified(IReadOnlyDictionary<int, SecretKey> unlocked, LevelsFile levels)
    {
        var verified = new Dictionary<int, SecretKey>(unlocked.Count);
        try
        {
            foreach (var (level, key) in unlocked)
            {
                if (level <= levels.Top && levels.Levels[level].Check is { } check && LevelCheck.Verify(key, check, level))
                {
                    verified.Add(level, key);
                }
                else
                {
                    key.Dispose();
                }
            }
        }
        catch
        {
            foreach (var key in unlocked.Values)
            {
                key.Dispose();
            }

            throw;
        }

        return verified;
    }
}

/// <summary>Why a sign-in gave no session (<see cref="SignIn.TryStart"/>).</summary>
public enum SignInRefusal
{
    /// <summary>The team has no user file of the account.</summary>
    NoUser,

    /// <summary>The user file of the account is damaged or does not fit its model.</summary>
    UserInvalid,

    /// <summary><c>levels.json</c> is missing, damaged or does not fit, so no key can be checked.</summary>
    LevelsInvalid,

    /// <summary>The user file has keys, and the password unlocks none of them.</summary>
    WrongPassword,

    /// <summary>The user file has no keys, or none of the keys that unlocked passed the check of its level (design 7.5).</summary>
    NoAccess,
}

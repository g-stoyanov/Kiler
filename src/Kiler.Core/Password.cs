using System.Security.Cryptography;

namespace Kiler.Core;

/// <summary>
/// The length of a password (decided at the start of M4): 8 to 1024 characters. Kiler.Crypto takes an empty one and throws
/// over 1024, so the API checks both before a key is derived (kiler-crypto, "Passwords"), and the screens too.
/// </summary>
public static class Password
{
    public const int MinLength = 8;

    /// <summary>The limit of Kiler.Crypto, <c>PasswordKey.MaxLength</c>, which is internal there; a test holds the two equal.</summary>
    public const int MaxLength = 1024;

    /// <summary>The alphabet of a temporary password: lowercase Crockford Base32 (decided at the start of M4).</summary>
    public const string TemporaryAlphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    /// <summary>The length of a temporary password: 16 characters of 5 bits, 80 bits.</summary>
    public const int TemporaryLength = 16;

    /// <summary>Why a password cannot be one, for the page; <c>null</c> when it can.</summary>
    public static string? Invalid(ReadOnlySpan<char> password) =>
        password.Length is < MinLength or > MaxLength ? $"A password has {MinLength} to {MaxLength} characters." : null;

    /// <summary>
    /// A new temporary password (design 7.6, decided at the start of M4), which Kiler makes and the admin does not type: random
    /// characters of <see cref="TemporaryAlphabet"/>, used as shown. It goes to the admin once, in the answer of the action
    /// that made it, and never into a file other than the locked keys, a log or a later answer (kiler-crypto, "Passwords").
    /// </summary>
    public static string NewTemporary() => RandomNumberGenerator.GetString(TemporaryAlphabet, TemporaryLength);
}

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

    /// <summary>Why a password cannot be one, for the page; <c>null</c> when it can.</summary>
    public static string? Invalid(ReadOnlySpan<char> password) =>
        password.Length is < MinLength or > MaxLength ? $"A password has {MinLength} to {MaxLength} characters." : null;
}

using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Kiler.App;

/// <summary>
/// The secret of one run of Kiler: 32 random bytes, made in the host and passed through DI, never through
/// the command line or the environment, which other processes of the user can read. The browser brings
/// it once in the address and then in a cookie (<see cref="AccessGuard"/>).
/// </summary>
internal sealed class AccessToken
{
    private const int Size = 32;

    private static readonly SearchValues<char> Alphabet =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    private readonly byte[] _text;

    public AccessToken()
        : this(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(Size)))
    {
    }

    private AccessToken(string value)
    {
        Value = value;
        _text = Encoding.UTF8.GetBytes(value);
    }

    /// <summary>The token as base64url without padding: 43 characters that need no escaping in an address or a cookie.</summary>
    public string Value { get; }

    /// <summary>
    /// Reads the token of a running Kiler from <c>instance.json</c>. Only the exact form of <see cref="Value"/> passes:
    /// the token goes into the address that the browser gets on its command line, so no other character may reach it.
    /// </summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out AccessToken? token)
    {
        token = null;

        // IsValid skips white space and accepts padding, so the length and the alphabet are checked first. It refuses
        // set unused bits in the last character, so no other text gives the same 32 bytes.
        if (value is null
            || value.Length != Base64Url.GetEncodedLength(Size)
            || value.AsSpan().ContainsAnyExcept(Alphabet)
            || !Base64Url.IsValid(value, out var decodedLength)
            || decodedLength != Size)
        {
            return false;
        }

        token = new AccessToken(value);
        return true;
    }

    /// <summary>Compares in constant time. Only a different length returns early, and the length is not secret.</summary>
    public bool Matches(string? candidate) =>
        candidate is not null
        && candidate.Length == Value.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), _text);
}

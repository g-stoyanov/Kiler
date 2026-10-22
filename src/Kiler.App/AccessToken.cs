using System.Buffers.Text;
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

    private readonly byte[] _text;

    public AccessToken()
    {
        Value = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(Size));
        _text = Encoding.UTF8.GetBytes(Value);
    }

    /// <summary>The token as base64url without padding: 43 characters that need no escaping in an address or a cookie.</summary>
    public string Value { get; }

    /// <summary>Compares in constant time. Only a different length returns early, and the length is not secret.</summary>
    public bool Matches(string? candidate) =>
        candidate is not null
        && candidate.Length == Value.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), _text);
}

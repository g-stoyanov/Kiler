using System.Security.Cryptography;
using System.Text;

namespace Kiler.Crypto;

/// <summary>
/// A key from a password or a recovery key (M3.1): PBKDF2-SHA256 with 600 000 iterations and a salt of 16 random bytes,
/// new at every lock. The password goes in as its UTF-8, as typed, and those bytes are zeroed after use.
/// </summary>
internal static class PasswordKey
{
    /// <summary>Part of the format: a lower count would read no key locked before.</summary>
    public const int Iterations = 600_000;

    public const int SaltSize = 16;

    /// <summary>The longest password, so a huge one never makes a buffer of its size.</summary>
    public const int MaxLength = 1024;

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    public static SecretKey Derive(ReadOnlySpan<char> password, ReadOnlySpan<byte> salt)
    {
        if (salt.Length != SaltSize)
        {
            throw new ArgumentException($"A salt has {SaltSize} bytes.", nameof(salt));
        }

        var bytes = GC.AllocateArray<byte>(SecretKey.Size, pinned: true);
        try
        {
            Derive(password, salt, Iterations, bytes);
            return SecretKey.FromBytes(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>PBKDF2-SHA256 of the UTF-8 of the password into <paramref name="destination"/>, with any salt and count: the test vectors take other ones.</summary>
    public static void Derive(ReadOnlySpan<char> password, ReadOnlySpan<byte> salt, int iterations, Span<byte> destination)
    {
        if (password.Length > MaxLength)
        {
            throw new ArgumentException($"A password has at most {MaxLength} characters.", nameof(password));
        }

        var utf8 = GC.AllocateArray<byte>(Encoding.UTF8.GetMaxByteCount(password.Length), pinned: true);
        try
        {
            var length = Encoding.UTF8.GetBytes(password, utf8);
            Rfc2898DeriveBytes.Pbkdf2(utf8.AsSpan(0, length), salt, destination, iterations, HashAlgorithmName.SHA256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
        }
    }
}

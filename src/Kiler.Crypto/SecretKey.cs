using System.Security.Cryptography;

namespace Kiler.Crypto;

/// <summary>A use of the bytes of a key: they are valid only during the call.</summary>
internal delegate T KeyUse<T>(ReadOnlySpan<byte> key);

/// <summary>
/// A key of 32 bytes: a level key, or a key derived from a password (M3.1). The bytes live in a pinned array, so the GC
/// never moves them and leaves no copies behind, and they are zeroed when the key is disposed. They are read only through
/// <see cref="Use"/>, under the lock that <see cref="Dispose"/> takes: the automatic locking (M4) can dispose a key while a
/// request encrypts with it, and without the lock that encryption would take the zeroed bytes as its key and succeed
/// (code and security review of M3.1). A use under way ends with the key; one after the dispose gets an
/// <see cref="ObjectDisposedException"/>. The bytes never leave the library; <see cref="object.ToString"/> is the type's name.
/// </summary>
public sealed class SecretKey : IDisposable
{
    public const int Size = 32;

    private readonly byte[] _bytes = GC.AllocateArray<byte>(Size, pinned: true);
    private readonly Lock _gate = new();
    private bool _disposed;

    private SecretKey()
    {
    }

    /// <summary>A new key of 32 random bytes.</summary>
    public static SecretKey New()
    {
        var key = new SecretKey();
        RandomNumberGenerator.Fill(key._bytes);
        return key;
    }

    /// <summary>A key with a copy of the bytes given; the caller zeroes its own.</summary>
    internal static SecretKey FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"A key has {Size} bytes.", nameof(bytes));
        }

        var key = new SecretKey();
        bytes.CopyTo(key._bytes);
        return key;
    }

    /// <summary>Runs <paramref name="use"/> with the bytes, which it must not keep: <see cref="AesGcm"/> copies them when it is made.</summary>
    internal T Use<T>(KeyUse<T> use)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return use(_bytes);
        }
    }

    /// <summary>The array itself, for the tests that see it zeroed.</summary>
    internal byte[] Buffer => _bytes;

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CryptographicOperations.ZeroMemory(_bytes);
        }
    }
}

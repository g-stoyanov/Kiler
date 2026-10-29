using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Kiler.Crypto;

/// <summary>
/// The recovery key (M3.3, design 7.7): 20 random bytes as 32 characters of Crockford Base32, shown once in 8 groups of 4
/// joined by "-". It locks the level keys as a password does (<see cref="LevelKeys"/>): the 32 characters in upper case are
/// the secret. A typed key is read without regard to case, hyphens and spaces, with O read as 0 and I and L as 1; any other
/// character, or another length, is refused. The characters live in a pinned array that is zeroed when the key is disposed.
/// A key serves one request, made by the wizard or read from what was typed, so it has no lock as a <see cref="SecretKey"/>
/// has. <see cref="object.ToString"/> is the type's name.
/// </summary>
public sealed class RecoveryKey : IDisposable
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private const int Size = 20;

    private const int Length = 32;

    private const int GroupSize = 4;

    private readonly char[] _chars = GC.AllocateArray<char>(Length, pinned: true);
    private bool _disposed;

    private RecoveryKey()
    {
    }

    public static RecoveryKey New()
    {
        var bytes = GC.AllocateArray<byte>(Size, pinned: true);
        try
        {
            RandomNumberGenerator.Fill(bytes);
            return FromBytes(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>The key typed by a user, or false and null for any other character or another length.</summary>
    public static bool TryParse(ReadOnlySpan<char> typed, [NotNullWhen(true)] out RecoveryKey? key)
    {
        var read = new RecoveryKey();
        var count = 0;
        foreach (var c in typed)
        {
            if (c is '-' or ' ')
            {
                continue;
            }

            var symbol = Symbol(c);
            if (symbol < 0 || count == Length)
            {
                count = -1;
                break;
            }

            read._chars[count++] = Alphabet[symbol];
        }

        if (count != Length)
        {
            read.Dispose();
            key = null;
            return false;
        }

        key = read;
        return true;
    }

    /// <summary>The key of 20 bytes: each 5 bits, from the highest bit of the first byte on, are one character.</summary>
    internal static RecoveryKey FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"A recovery key has {Size} bytes.", nameof(bytes));
        }

        var key = new RecoveryKey();

        // Each 5 bytes are 40 bits, 8 characters.
        for (var block = 0; block < Size / 5; block++)
        {
            ulong bits = 0;
            foreach (var b in bytes.Slice(block * 5, 5))
            {
                bits = (bits << 8) | b;
            }

            for (var i = 0; i < 8; i++)
            {
                key._chars[(block * 8) + i] = Alphabet[(int)(bits >> (35 - (5 * i))) & 31];
            }
        }

        return key;
    }

    /// <summary>The key as it is shown once: 8 groups of 4 characters joined by "-".</summary>
    public string ToDisplayString()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return string.Create(Length + (Length / GroupSize) - 1, _chars, static (display, chars) =>
        {
            var at = 0;
            for (var i = 0; i < chars.Length; i++)
            {
                if (i > 0 && i % GroupSize == 0)
                {
                    display[at++] = '-';
                }

                display[at++] = chars[i];
            }
        });
    }

    /// <summary>The 32 characters in upper case, the secret of a lock. They are valid until the key is disposed.</summary>
    internal ReadOnlySpan<char> Secret
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _chars;
        }
    }

    /// <summary>The array itself, for the tests that see it zeroed.</summary>
    internal char[] Buffer => _chars;

    public void Dispose()
    {
        _disposed = true;
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_chars.AsSpan()));
    }

    /// <summary>The place in the alphabet of a typed character, or -1: an ASCII letter in either case, with O as 0 and I and L as 1.</summary>
    private static int Symbol(char c)
    {
        var upper = c is >= 'a' and <= 'z' ? (char)(c - 'a' + 'A') : c;
        return upper switch
        {
            'O' => 0,
            'I' or 'L' => 1,
            _ => Alphabet.IndexOf(upper, StringComparison.Ordinal),
        };
    }
}

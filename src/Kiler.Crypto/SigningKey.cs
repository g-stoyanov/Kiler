using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Kiler.Crypto;

/// <summary>
/// The key that signs the releases (M3 step 11, M3.4): a key pair of ECDSA over P-256, made once by the maintainer. Only its
/// public key is in the exe. The private key lives only in a file locked with a password, kept by the maintainer with a copy
/// of the file and its password in the company password manager (decided on 2026-11-11), never in the repo, the share, the
/// exe, a test, CI, a console line or a log.
/// <para>
/// The text of the file is one line: <c>kiler-signing-key.1.&lt;salt&gt;.&lt;public key&gt;.&lt;locked key&gt;</c>, decided
/// in M3.4. The salt is the base64 of 16 bytes, new at every lock; the public key is that of <see cref="PublicKey"/>; the
/// locked key is the PKCS#8 of the key as an encrypted value with the key of the password and the salt, the purpose
/// "signing-key" and the public key as its context. It is not the encrypted PKCS#8 of .NET, whose PBES2 is AES-CBC without
/// a MAC. Kiler.Crypto knows no files: the release tool writes and reads the text.
/// </para>
/// The private key never leaves the class; <see cref="object.ToString"/> is the type's name. One thread uses a key, so it
/// has no lock: only the release tool signs.
/// </summary>
public sealed class SigningKey : IDisposable
{
    /// <summary>The shortest password of the file: it can be attacked offline once copied (decided in M3.4).</summary>
    public const int MinPasswordLength = 16;

    private const string Label = "kiler-signing-key";

    private const string Format1 = "1";

    private const int Parts = 5;

    private readonly ECDsa _key;
    private bool _disposed;

    private SigningKey(ECDsa key)
    {
        _key = key;
        PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>The base64 of the SubjectPublicKeyInfo of the key, which <see cref="UpdateSignature.Verify"/> takes.</summary>
    public string PublicKey { get; }

    /// <summary>A new key pair of P-256.</summary>
    public static SigningKey New() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>The signature of <paramref name="version"/> and <paramref name="sha256"/>, for <c>version.json</c>.</summary>
    public string Sign(ReleaseVersion version, ReadOnlySpan<byte> sha256)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return UpdateSignature.Sign(_key, UpdateSignature.SignedText(version, sha256));
    }

    /// <summary>
    /// The text of the key file, with the key locked with <paramref name="password"/>, which has at least
    /// <see cref="MinPasswordLength"/> characters and at most <see cref="PasswordKey.MaxLength"/>.
    /// </summary>
    public string Lock(ReadOnlySpan<char> password)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (password.Length < MinPasswordLength)
        {
            throw new ArgumentException($"A password of the signing key has at least {MinPasswordLength} characters.", nameof(password));
        }

        var salt = PasswordKey.NewSalt();
        using var passwordKey = PasswordKey.Derive(password, salt);

        // A PKCS#8 of P-256 with its public key has about 140 bytes.
        var pkcs8 = GC.AllocateArray<byte>(512, pinned: true);
        try
        {
            if (!_key.TryExportPkcs8PrivateKey(pkcs8, out var length))
            {
                throw new CryptographicException("The signing key cannot be exported.");
            }

            var locked = EncryptedValue.Encrypt(passwordKey, pkcs8.AsSpan(0, length), Purpose.SigningKey, PublicKey);
            return string.Join('.', Label, Format1, Convert.ToBase64String(salt), PublicKey, locked);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    /// <summary>
    /// The key of the text of a key file that <paramref name="password"/> unlocks, which the caller disposes. White space at
    /// the ends is taken, as an editor or a password manager can add a line break. A wrong password, one longer than
    /// <see cref="PasswordKey.MaxLength"/>, and a file that is not of this format, is damaged, or holds a key that is not of
    /// P-256 or not of its public key are false: one answer, as the tag of AES-GCM does not tell a wrong password from a
    /// damaged file.
    /// </summary>
    public static bool TryUnlock(string file, ReadOnlySpan<char> password, [NotNullWhen(true)] out SigningKey? key)
    {
        ArgumentNullException.ThrowIfNull(file);
        key = null;

        // Lock refuses a longer password, so it locked no file, and PasswordKey.Derive would throw for it.
        var parts = file.Trim().Split('.');
        if (password.Length > PasswordKey.MaxLength
            || parts.Length != Parts
            || !string.Equals(parts[0], Label, StringComparison.Ordinal)
            || !string.Equals(parts[1], Format1, StringComparison.Ordinal)
            || !CanonicalBase64.TryDecode(parts[2], out var salt)
            || salt.Length != PasswordKey.SaltSize
            || !CanonicalBase64.TryDecode(parts[4], out var locked)
            || locked.Length < EncryptedValue.MinSize)
        {
            return false;
        }

        var publicKey = parts[3];
        using (var check = UpdateSignature.ImportPublicKey(publicKey))
        {
            if (check is null)
            {
                return false;
            }
        }

        using var passwordKey = PasswordKey.Derive(password, salt);
        var pkcs8 = GC.AllocateArray<byte>(locked.Length - EncryptedValue.MinSize, pinned: true);
        try
        {
            EncryptedValue.Decrypt(passwordKey, parts[4], pkcs8, Purpose.SigningKey, publicKey);
            key = Import(pkcs8, publicKey);
            return key is not null;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _key.Dispose();
    }

    /// <summary>
    /// The key of a whole PKCS#8 whose public key is <paramref name="publicKey"/>, which the caller checked is of P-256;
    /// anything else is null.
    /// </summary>
    private static SigningKey? Import(ReadOnlySpan<byte> pkcs8, string publicKey)
    {
        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportPkcs8PrivateKey(pkcs8, out var read);
            if (read == pkcs8.Length && string.Equals(Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()), publicKey, StringComparison.Ordinal))
            {
                return new SigningKey(ecdsa);
            }
        }
        catch (Exception exception) when (UpdateSignature.IsNotAKey(exception))
        {
        }

        ecdsa.Dispose();
        return null;
    }
}

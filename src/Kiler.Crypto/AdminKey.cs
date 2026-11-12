using System.Security.Cryptography;

namespace Kiler.Crypto;

/// <summary>
/// The admin key (M3 step 6, M3.9): a key pair of ECDSA over P-256 that the wizard makes once (decided on 2026-11-12). Its
/// private key seals the rights of the user files, <c>levels.json</c>, <c>settings.json</c> and the templates
/// (<see cref="AdminSeal"/>). Only admins have it, locked with their password in their user file, and the recovery set of
/// <c>levels.json</c> has it locked with the recovery key; every user has its public key, locked with their password with
/// the level keys (<see cref="LevelKeys.Lock(IReadOnlyDictionary{int, SecretKey}, string, AdminKey?, ReadOnlySpan{char})"/>).
/// <para>
/// The private key never leaves the class but locked; <see cref="object.ToString"/> is the type's name. A session keeps the
/// key, and the automatic locking (M4) can dispose it while a request seals with it, so a seal and a lock hold the lock
/// that <see cref="Dispose"/> takes, as <see cref="SecretKey.Use{T}"/> does.
/// </para>
/// </summary>
public sealed class AdminKey : IDisposable
{
    private readonly ECDsa _key;
    private readonly Lock _gate = new();
    private bool _disposed;

    private AdminKey(ECDsa key)
    {
        _key = key;
        PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>The base64 of the SubjectPublicKeyInfo of the key, which <see cref="AdminSeal.Verify"/> takes.</summary>
    public string PublicKey { get; }

    /// <summary>A new key pair of P-256.</summary>
    public static AdminKey New() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>The seal of <paramref name="text"/> for <paramref name="purpose"/>.</summary>
    public string Seal(SealPurpose purpose, ReadOnlySpan<byte> text)
    {
        var signed = AdminSeal.SignedText(purpose, text);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return EcdsaP256.Sign(_key, signed);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _key.Dispose();
        }
    }

    /// <summary>The private key locked with <paramref name="secretKey"/>: its PKCS#8, with its public key as the context.</summary>
    internal string Lock(SecretKey secretKey)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return EcdsaP256.LockPrivateKey(_key, secretKey, Purpose.AdminPrivateKey, PublicKey);
        }
    }

    /// <summary>
    /// The key that <paramref name="value"/> holds locked with <paramref name="secretKey"/>, when its public key is
    /// <paramref name="publicKey"/>, a public key of P-256 that the caller unlocked; anything else is null.
    /// </summary>
    internal static AdminKey? Unlock(SecretKey secretKey, string value, string publicKey) =>
        EcdsaP256.UnlockPrivateKey(secretKey, value, Purpose.AdminPrivateKey, publicKey) is { } key ? new AdminKey(key) : null;
}

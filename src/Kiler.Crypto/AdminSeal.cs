using System.Buffers.Binary;

namespace Kiler.Crypto;

/// <summary>
/// What a seal is of (M3.9). Its label goes into the sealed text, so the seal of one file is never taken as that of another
/// kind of file whose text has the same bytes. The labels are part of the format.
/// </summary>
public enum SealPurpose
{
    /// <summary>The rights of a user file: <c>user</c>.</summary>
    UserFile,

    /// <summary><c>levels.json</c>: <c>levels</c>.</summary>
    Levels,

    /// <summary><c>settings.json</c>: <c>settings</c>.</summary>
    Settings,

    /// <summary>A template: <c>template</c>.</summary>
    Template,
}

/// <summary>
/// The admin seal (M3 step 6, M3.9): ECDSA over P-256 with SHA-256 by the private key of the admin key, checked by every
/// Kiler with its public key (decided on 2026-11-12, instead of the HMAC of design 7.3). The sealed text is the format byte,
/// then the label "seal", the label of the <see cref="SealPurpose"/> and the text, each with its length before it in 4 bytes,
/// big-endian, as the associated data of an encrypted value. The text of each file is Kiler.Core's; Kiler.Crypto seals and
/// checks bytes. The public key is the base64 of its SubjectPublicKeyInfo, and the seal the base64 of its 64 bytes in the
/// form of IEEE P1363, as an update signature (<see cref="UpdateSignature"/>, whose label keeps the two apart).
/// </summary>
public static class AdminSeal
{
    /// <summary>The first byte of the sealed text: ECDSA over P-256 with SHA-256. A new algorithm gets a new format.</summary>
    internal const byte Format1 = 1;

    /// <summary>
    /// Whether <paramref name="seal"/> is a seal of <paramref name="text"/> for <paramref name="purpose"/> by the admin key of
    /// <paramref name="publicKey"/>. A key that is not a public key of P-256, or a seal that is not 64 bytes, each in its one
    /// base64 form, gives false. An unknown purpose is a bug of the caller and throws.
    /// </summary>
    public static bool Verify(string publicKey, SealPurpose purpose, ReadOnlySpan<byte> text, string seal)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(seal);
        return EcdsaP256.Verify(publicKey, SignedText(purpose, text), seal);
    }

    /// <summary>The text that a seal signs.</summary>
    internal static byte[] SignedText(SealPurpose purpose, ReadOnlySpan<byte> text)
    {
        var head = EncryptedValue.AssociatedData(Format1, Purpose.Seal, [Label(purpose)]);
        var signed = new byte[head.Length + sizeof(int) + text.Length];
        head.CopyTo(signed, 0);
        BinaryPrimitives.WriteInt32BigEndian(signed.AsSpan(head.Length), text.Length);
        text.CopyTo(signed.AsSpan(head.Length + sizeof(int)));
        return signed;
    }

    private static string Label(SealPurpose purpose) => purpose switch
    {
        SealPurpose.UserFile => "user",
        SealPurpose.Levels => "levels",
        SealPurpose.Settings => "settings",
        SealPurpose.Template => "template",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };
}

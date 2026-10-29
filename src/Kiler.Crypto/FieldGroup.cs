namespace Kiler.Crypto;

/// <summary>
/// The group of fields of one level of a shared record (M3.2): its <c>protected."&lt;level&gt;"</c> (design 5.2), encrypted
/// with the key of the level. Kiler.Core gives the UTF-8 of the fields and keeps the value. The record id and the level are
/// the context (PLAN section 6), so a group cannot be moved to another record or level.
/// </summary>
public static class FieldGroup
{
    public static string Encrypt(SecretKey key, ReadOnlySpan<byte> fields, string recordId, int level) =>
        EncryptedValue.Encrypt(key, fields, Purpose.Group, RecordId(recordId), Level.Context(level));

    /// <summary>
    /// The UTF-8 of the fields, which hold secrets, so the caller zeroes it after use. A group that is damaged, or is read
    /// with another key, record id or level, is a <see cref="System.Security.Cryptography.CryptographicException"/>.
    /// </summary>
    public static byte[] Decrypt(SecretKey key, string value, string recordId, int level) =>
        EncryptedValue.Decrypt(key, value, Purpose.Group, RecordId(recordId), Level.Context(level));

    private static string RecordId(string recordId)
    {
        ArgumentException.ThrowIfNullOrEmpty(recordId);
        return recordId;
    }
}

namespace Kiler.Crypto.Tests;

internal static class TestKeys
{
    /// <summary>A copy of the bytes of a key, to compare in a test.</summary>
    public static byte[] Copy(this SecretKey key) => key.Use(bytes => bytes.ToArray());
}

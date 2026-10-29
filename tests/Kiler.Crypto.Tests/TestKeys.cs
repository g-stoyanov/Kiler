namespace Kiler.Crypto.Tests;

internal static class TestKeys
{
    /// <summary>A copy of the bytes of a key, to compare in a test.</summary>
    public static byte[] Copy(this SecretKey key) => key.Use(bytes => bytes.ToArray());
}

/// <summary>The keys that an unlock gives, disposed at the end of a test's <c>using</c>.</summary>
internal sealed class KeySet(IReadOnlyDictionary<int, SecretKey> keys) : IDisposable
{
    public IReadOnlyDictionary<int, SecretKey> Keys => keys;

    /// <summary>The levels in order, to compare: they are not secret.</summary>
    public int[] Levels => [.. keys.Keys.Order()];

    public SecretKey this[int level] => keys[level];

    public void Dispose()
    {
        foreach (var key in keys.Values)
        {
            key.Dispose();
        }
    }
}

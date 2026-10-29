namespace Kiler.Crypto.Tests;

public class SecretKeyTests
{
    [Fact]
    public void New_GivesThirtyTwoRandomBytes()
    {
        using var first = SecretKey.New();
        using var second = SecretKey.New();

        Assert.Equal(SecretKey.Size, first.Copy().Length);
        Assert.False(first.Copy().SequenceEqual(second.Copy()), "Two new keys are the same.");
        Assert.False(first.Copy().All(b => b == 0), "A new key is all zeros.");
    }

    [Fact]
    public void FromBytes_KeepsACopyOfTheBytes()
    {
        var bytes = Enumerable.Range(0, SecretKey.Size).Select(i => (byte)i).ToArray();

        using var key = SecretKey.FromBytes(bytes);
        bytes[0] = 0xFF;

        Assert.True(key.Copy()[0] == 0, "The key follows a change of the bytes it was made from.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void FromBytes_OfAnotherLength_IsRefused(int length) =>
        Assert.Throws<ArgumentException>(() => SecretKey.FromBytes(new byte[length]));

    [Fact]
    public void Dispose_ZeroesTheKey()
    {
        var key = SecretKey.New();

        key.Dispose();

        Assert.True(key.Buffer.All(b => b == 0), "The key was not zeroed.");
    }

    [Fact]
    public void ADisposedKey_CannotBeUsed()
    {
        var key = SecretKey.New();

        key.Dispose();
        key.Dispose();

        Assert.Throws<ObjectDisposedException>(() => key.Use(bytes => bytes.Length));
        Assert.Throws<ObjectDisposedException>(() => EncryptedValue.Encrypt(key, [1, 2, 3], Purpose.Check, "1"));
        Assert.Throws<ObjectDisposedException>(() => EncryptedValue.Decrypt(key, "not a value", Purpose.Check, "1"));
    }

    // The automatic locking (M4) can dispose a key while a request encrypts with it.
    [Fact]
    public async Task Dispose_WaitsForAUseUnderWay()
    {
        var token = TestContext.Current.CancellationToken;
        var key = SecretKey.New();
        using var inUse = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var use = Task.Run(
            () => key.Use(bytes =>
            {
                inUse.Set();
                release.Wait(token);
                return bytes.ToArray().Any(b => b != 0);
            }),
            token);
        inUse.Wait(token);
        var dispose = Task.Run(key.Dispose, token);

        Assert.NotSame(dispose, await Task.WhenAny(dispose, Task.Delay(200, token)));
        release.Set();

        Assert.True(await use, "The key was zeroed during its use.");
        await dispose;
        Assert.True(key.Buffer.All(b => b == 0), "The key was not zeroed after its use.");
    }
}

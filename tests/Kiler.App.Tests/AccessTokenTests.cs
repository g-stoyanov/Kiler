using System.Buffers.Text;

namespace Kiler.App.Tests;

public class AccessTokenTests
{
    [Fact]
    public void NewToken_Is32BytesAsBase64UrlWithoutPadding()
    {
        var token = new AccessToken();

        Assert.Matches("^[A-Za-z0-9_-]{43}$", token.Value);
        Assert.Equal(32, Base64Url.DecodeFromChars(token.Value).Length);
    }

    [Fact]
    public void TwoNewTokens_AreDifferent()
    {
        Assert.NotEqual(new AccessToken().Value, new AccessToken().Value);
    }

    [Fact]
    public void Matches_ItsOwnValue_InAnotherStringInstance()
    {
        var token = new AccessToken();

        Assert.True(token.Matches(new string(token.Value.AsSpan())));
    }

    [Fact]
    public void Matches_AnyOtherValue_IsFalse()
    {
        var token = new AccessToken();
        var value = token.Value;
        var lastChanged = value[..^1] + (value[^1] == 'A' ? 'B' : 'A');

        Assert.False(token.Matches(null));
        Assert.False(token.Matches(""));
        Assert.False(token.Matches(new AccessToken().Value));
        Assert.False(token.Matches(value[..^1]));
        Assert.False(token.Matches(value + "A"));
        Assert.False(token.Matches(lastChanged));
        Assert.False(token.Matches(value[..^1] + "é"));
    }

    [Fact]
    public void ToString_DoesNotShowTheValue()
    {
        var token = new AccessToken();

        Assert.DoesNotContain(token.Value, token.ToString(), StringComparison.Ordinal);
    }
}

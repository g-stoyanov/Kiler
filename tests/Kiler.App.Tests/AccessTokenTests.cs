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
    public void TryParse_TheValueOfAToken_GivesATokenThatMatchesIt()
    {
        var original = new AccessToken();

        Assert.True(AccessToken.TryParse(original.Value, out var parsed));

        Assert.Equal(original.Value, parsed.Value);
        Assert.True(parsed.Matches(original.Value));
    }

    [Fact]
    public void TryParse_AnythingButTheExactForm_IsFalse()
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var value = new AccessToken().Value;
        // 43 characters carry 258 bits for 256, so the last character has two unused bits. Setting one gives the same
        // bytes from another text.
        var unusedBitSet = value[..^1] + Alphabet[Alphabet.IndexOf(value[^1], StringComparison.Ordinal) + 1];
        string?[] invalid =
        [
            null,
            "",
            value[..^1],
            value + "A",
            value + "=",
            " " + value,
            value + " ",
            value[..20] + " " + value[21..],
            value[..20] + "\r\n" + value[22..],
            value[..^1] + "+",
            value[..^1] + "/",
            value[..^1] + "\"",
            value[..^1] + "é",
            "\" --flag " + value[9..],
            unusedBitSet,
        ];

        Assert.All(invalid, candidate => Assert.False(AccessToken.TryParse(candidate, out _), candidate));
    }

    [Fact]
    public void ToString_DoesNotShowTheValue()
    {
        var token = new AccessToken();

        Assert.DoesNotContain(token.Value, token.ToString(), StringComparison.Ordinal);
    }
}

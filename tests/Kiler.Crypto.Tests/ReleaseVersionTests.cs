namespace Kiler.Crypto.Tests;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("0.0.0", 0, 0, 0)]
    [InlineData("1.0.0", 1, 0, 0)]
    [InlineData("1.10.0", 1, 10, 0)]
    [InlineData("2147483647.0.1", int.MaxValue, 0, 1)]
    public void TryParse_AVersionOfThreeNumbers_GivesItsParts(string text, int major, int minor, int patch)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));

        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(text, version.ToString());
    }

    // The text of a version is signed, so a version has one text: no other form of the same numbers is read.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1..0")]
    [InlineData("1.0.")]
    [InlineData(".1.0")]
    [InlineData("01.0.0")]
    [InlineData("1.00.0")]
    [InlineData("1.0.00")]
    [InlineData(" 1.0.0")]
    [InlineData("1.0.0 ")]
    [InlineData("1. 0.0")]
    [InlineData("+1.0.0")]
    [InlineData("-1.0.0")]
    [InlineData("1,0,0")]
    [InlineData("a.b.c")]
    [InlineData("1.0.0-beta")]
    [InlineData("1.0.0+abc")]
    [InlineData("2147483648.0.0")]
    [InlineData("1.0.99999999999")]
    public void TryParse_NotAVersionOfThreeNumbers_IsFalse(string? text)
    {
        Assert.False(ReleaseVersion.TryParse(text, out var version));
        Assert.Equal(default, version);
    }

    // An Arabic-Indic one and a full-width zero, built in code: the tools turn an escape in the source into the character.
    [Theory]
    [InlineData(0x0661, 0)]
    [InlineData(0xFF10, 2)]
    public void TryParse_ADigitOfAnotherScript_IsFalse(int digit, int at)
    {
        var text = "1.0.0".Remove(at, 1).Insert(at, ((char)digit).ToString());

        Assert.False(ReleaseVersion.TryParse(text, out _));
    }

    [Theory]
    [InlineData("1.10.0", "1.9.0")]
    [InlineData("1.0.10", "1.0.9")]
    [InlineData("10.0.0", "9.0.0")]
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("1.1.0", "1.0.99")]
    [InlineData("1.0.1", "1.0.0")]
    [InlineData("0.0.1", "0.0.0")]
    public void CompareTo_ComparesByNumberMajorFirst(string higherText, string lowerText)
    {
        Assert.True(ReleaseVersion.TryParse(higherText, out var higher));
        Assert.True(ReleaseVersion.TryParse(lowerText, out var lower));

        Assert.True(higher.CompareTo(lower) > 0);
        Assert.True(lower.CompareTo(higher) < 0);
        Assert.True(higher > lower);
        Assert.True(higher >= lower);
        Assert.True(lower < higher);
        Assert.True(lower <= higher);
        Assert.False(higher < lower);
        Assert.False(lower > higher);
        Assert.NotEqual(higher, lower);
        Assert.True(higher != lower);
    }

    [Fact]
    public void CompareTo_TheSameVersion_IsEqual()
    {
        Assert.True(ReleaseVersion.TryParse("1.2.3", out var parsed));
        var made = new ReleaseVersion(1, 2, 3);

        Assert.Equal(0, parsed.CompareTo(made));
        Assert.Equal(made, parsed);
        Assert.True(parsed == made);
        Assert.True(parsed >= made);
        Assert.True(parsed <= made);
        Assert.False(parsed > made);
        Assert.False(parsed < made);
        Assert.Equal(made.GetHashCode(), parsed.GetHashCode());
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, -1)]
    public void New_ANegativePart_IsRefused(int major, int minor, int patch)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReleaseVersion(major, minor, patch));
    }
}

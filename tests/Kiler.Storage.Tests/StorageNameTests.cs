namespace Kiler.Storage.Tests;

public class StorageNameTests
{
    [Theory]
    [InlineData("8f3a1c")]
    [InlineData("demo.user")]
    [InlineData("db")]
    [InlineData("v6.json")]
    [InlineData("a-b_c.d")]
    [InlineData("0")]
    // They start with a device name of Windows but are not one.
    [InlineData("console")]
    [InlineData("com10")]
    [InlineData("nul-1")]
    [InlineData("demo.con")]
    // They have "tmp" in them but do not end with ".tmp".
    [InlineData("tmp")]
    [InlineData("tmp.a")]
    [InlineData("a.tmpx")]
    [InlineData("a-tmp")]
    public void IsValid_AName_IsAccepted(string name) => Assert.True(StorageName.IsValid(name));

    [Fact]
    public void IsValid_ANameOfTheLongestLength_IsAccepted() => Assert.True(StorageName.IsValid(new string('a', StorageName.MaxLength)));

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"a\b")]
    [InlineData("a/b")]
    [InlineData(@"..\a")]
    [InlineData("C:x")]
    [InlineData("c:x")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a<b")]
    [InlineData("a|b")]
    [InlineData("a\"b")]
    // The device names of Windows, also with an extension.
    [InlineData("con")]
    [InlineData("NUL.json")]
    [InlineData("nul.json")]
    [InlineData("com1")]
    [InlineData("com0")]
    [InlineData("lpt9.a.b")]
    [InlineData("aux")]
    [InlineData("prn.json")]
    // Dots and spaces, which Windows drops at the end of a name.
    [InlineData("a.")]
    [InlineData(".a")]
    [InlineData("a..b")]
    [InlineData("a ")]
    [InlineData(" a")]
    [InlineData("a b")]
    // Control characters.
    [InlineData("a\u0001b")]
    [InlineData("a\tb")]
    [InlineData("a\u007fb")]
    // Anything outside lowercase ASCII.
    [InlineData("Demo")]
    [InlineData("DEMO.USER")]
    [InlineData("демо")]
    [InlineData("café")]
    [InlineData("a∕b")]
    // The ending of temporary files and folders.
    [InlineData("a.tmp")]
    [InlineData("demo.user.1a2b3c4d.tmp")]
    public void IsValid_AName_IsRefused(string name) => Assert.False(StorageName.IsValid(name));

    [Fact]
    public void IsValid_ANameLongerThanTheLimit_IsRefused() => Assert.False(StorageName.IsValid(new string('a', StorageName.MaxLength + 1)));

    [Fact]
    public void IsValid_Null_IsRefused() => Assert.False(StorageName.IsValid(null));

    [Fact]
    public void Check_AnAcceptedName_GivesItBack() => Assert.Equal("demo.user", StorageName.Check("demo.user"));

    [Fact]
    public void Check_ARefusedName_ThrowsWithoutRepeatingTheName()
    {
        // A name from the shared folder can hold anything, so the message does not carry it.
        var account = "made-up\u0001name";

        var refused = Assert.Throws<ArgumentException>(() => StorageName.Check(account));

        Assert.Equal("account", refused.ParamName);
        Assert.DoesNotContain("made-up", refused.Message, StringComparison.Ordinal);
    }
}

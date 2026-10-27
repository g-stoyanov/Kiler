using System.Globalization;

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

    [Theory]
    [InlineData("8f3a1c.json")]
    [InlineData("Demo file (2).JSON")]
    [InlineData("a~b!c@d#e$f%g^h&i'j[k]l{m}n")]
    public void Escape_PrintableAscii_StaysAsItIs(string name) => Assert.Equal(name, StorageName.Escape(name));

    [Fact]
    public void Escape_ABackslash_IsDoubled() => Assert.Equal(@"a\\b", StorageName.Escape(@"a\b"));

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x09)]
    [InlineData(0x1F)]
    [InlineData(0x7F)]
    [InlineData(0xA0)]
    // A Cyrillic letter, the right-to-left mark and override, a byte order mark, and a lone surrogate.
    [InlineData(0x0437)]
    [InlineData(0x200F)]
    [InlineData(0x202E)]
    [InlineData(0xFEFF)]
    [InlineData(0xD800)]
    public void Escape_AnyOtherCharacter_IsAnEscapeWithItsCode(int code) =>
        Assert.Equal("a" + Code(code) + "b", StorageName.Escape("a" + (char)code + "b"));

    [Fact]
    public void Escape_ACharacterOutsideTheBasicPlane_IsTheEscapesOfItsTwoHalves() =>
        Assert.Equal(Code(0xD83D) + Code(0xDE00), StorageName.Escape(char.ConvertFromUtf32(0x1F600)));

    [Fact]
    public void Escape_ANameOfTheLongestLength_IsNotCut() =>
        Assert.Equal(new string('A', StorageName.MaxLength), StorageName.Escape(new string('A', StorageName.MaxLength)));

    [Fact]
    public void Escape_ALongerName_IsCutAtTheLongestLength() =>
        Assert.Equal(new string('A', StorageName.MaxLength) + "...", StorageName.Escape(new string('A', 255)));

    [Fact]
    public void Escape_ALongName_IsCutBeforeItsEscapes() =>
        Assert.Equal(string.Concat(Enumerable.Repeat(Code(0x202E), StorageName.MaxLength)) + "...", StorageName.Escape(new string((char)0x202E, 200)));

    /// <summary>
    /// The escape of a character, built in code: the Write and Edit tools turn such an escape in their text into the
    /// character (M2.2).
    /// </summary>
    internal static string Code(int code) => @"\u" + code.ToString("x4", CultureInfo.InvariantCulture);
}

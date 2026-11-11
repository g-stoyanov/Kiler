using System.Text;

namespace Kiler.Release.Tests;

public class PasswordInputTests
{
    private const int BufferLength = 1025;

    [Theory]
    [InlineData("demo release password\n")]
    [InlineData("demo release password\r\n")]
    [InlineData("demo release password")]
    public void ReadLine_GivesTheLineWithoutItsEnd(string input)
    {
        Assert.Equal("demo release password", Read(Encoding.UTF8.GetBytes(input)));
    }

    [Fact]
    public void ReadLine_ReadsNoByteOfTheNextLine()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("first password\nsecond password\n"));
        var buffer = new char[BufferLength];

        var first = PasswordInput.ReadLine(input, buffer);
        Assert.Equal("first password", new string(buffer, 0, first));
        var second = PasswordInput.ReadLine(input, buffer);
        Assert.Equal("second password", new string(buffer, 0, second));
        Assert.Equal(-1, PasswordInput.ReadLine(input, buffer));
    }

    [Fact]
    public void ReadLine_TakesOffTheByteOrderMarkThatWindowsPowerShellPutsFirst()
    {
        // The bytes that Windows PowerShell 5.1 with $OutputEncoding of UTF-8 gave an exe for "ab`ncd`n" (M3.5).
        using var input = new MemoryStream([0xEF, 0xBB, 0xBF, 0x61, 0x62, 0x0A, 0x63, 0x64, 0x0A, 0x0D, 0x0A]);
        var buffer = new char[BufferLength];

        Assert.Equal("ab", new string(buffer, 0, PasswordInput.ReadLine(input, buffer)));
        Assert.Equal("cd", new string(buffer, 0, PasswordInput.ReadLine(input, buffer)));
    }

    [Fact]
    public void ReadLine_ReadsUtf8()
    {
        Assert.Equal("парола за пускането 🔑", Read(Encoding.UTF8.GetBytes("парола за пускането 🔑\n")));
    }

    [Fact]
    public void ReadLine_AnEmptyLine_IsEmpty()
    {
        Assert.Equal(string.Empty, Read("\n"u8.ToArray()));
    }

    [Fact]
    public void ReadLine_AnInputThatEnded_IsMinusOne()
    {
        Assert.Equal(-1, PasswordInput.ReadLine(new MemoryStream(), new char[BufferLength]));
    }

    [Fact]
    public void ReadLine_BytesThatAreNotUtf8_AreMinusOne()
    {
        Assert.Equal(-1, PasswordInput.ReadLine(new MemoryStream([0x70, 0xC3, 0x28, 0x0A]), new char[BufferLength]));
    }

    [Theory]
    [InlineData(1024, 1024)]
    [InlineData(1025, BufferLength)]
    [InlineData(1026, BufferLength)]
    [InlineData(2000, BufferLength)]
    [InlineData(4000, BufferLength)]
    public void ReadLine_ALongLine_GivesTheLengthOfTheBufferWhenItIsFull(int length, int expected)
    {
        Assert.Equal(expected, PasswordInput.ReadLine(new MemoryStream(Encoding.UTF8.GetBytes(new string('p', length) + "\n")), new char[BufferLength]));
    }

    [Theory]
    [InlineData('ж')]
    [InlineData('€')]
    public void ReadLine_AfterALineTooLong_ReadsTheNextLine(char character)
    {
        // A line of 3 bytes a character fills the buffer of bytes in the middle of a character.
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(new string(character, 4000) + "\nnext\n"));
        var buffer = new char[BufferLength];

        Assert.Equal(BufferLength, PasswordInput.ReadLine(input, buffer));
        Assert.Equal("next", new string(buffer, 0, PasswordInput.ReadLine(input, buffer)));
    }

    private static string Read(byte[] input)
    {
        var buffer = new char[BufferLength];
        var length = PasswordInput.ReadLine(new MemoryStream(input), buffer);
        Assert.True(length >= 0, "No line was read.");
        return new string(buffer, 0, length);
    }
}

using System.Security.Cryptography;
using System.Text;

namespace Kiler.Release;

/// <summary>What the tool says and asks (M3.5). The tests give a fake.</summary>
internal interface IToolConsole
{
    /// <summary>A line of the result, on the standard output.</summary>
    void Out(string line);

    /// <summary>A line of a refusal, on the standard error.</summary>
    void Error(string line);

    /// <summary>Whether the input is not a console, so a password comes as a line of it (<see cref="ReleaseTool.PasswordFromInput"/>).</summary>
    bool InputRedirected { get; }

    /// <summary>
    /// The password typed after <paramref name="prompt"/>, without echo, into <paramref name="buffer"/>. Gives its length; the
    /// length of the buffer when it is that long or longer, so a buffer one longer than the longest password tells a password
    /// that is too long; or -1 when the input ended with no password, or is not one that the tool can read.
    /// </summary>
    int ReadPassword(string prompt, Span<char> buffer);
}

/// <summary>
/// The console of the tool. A password is typed without echo through <see cref="Console.ReadKey(bool)"/>; when the input is
/// redirected, which <see cref="ReleaseTool"/> takes only with <see cref="ReleaseTool.PasswordFromInput"/>, as in the tests of
/// the exe, it is one line of the input as UTF-8 (<see cref="PasswordInput.ReadLine"/>). The prompts go to the standard
/// error, so the standard output holds only the result.
/// </summary>
internal sealed class SystemConsole : IToolConsole
{
    public static readonly SystemConsole Instance = new();

    private Stream? _input;

    private SystemConsole()
    {
    }

    public void Out(string line) => Console.Out.WriteLine(line);

    public void Error(string line) => Console.Error.WriteLine(line);

    public bool InputRedirected => Console.IsInputRedirected;

    public int ReadPassword(string prompt, Span<char> buffer)
    {
        Console.Error.Write(prompt);
        try
        {
            // One stream for the whole run: keygen reads two lines, and the stream reads no byte past a line.
            return Console.IsInputRedirected
                ? PasswordInput.ReadLine(_input ??= Console.OpenStandardInput(), buffer)
                : PasswordInput.ReadKeys(buffer);
        }
        finally
        {
            Console.Error.WriteLine();
        }
    }
}

/// <summary>How a password is read into a buffer of <see cref="char"/>, never into a <see cref="string"/> (M3.5).</summary>
internal static class PasswordInput
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The byte order mark of UTF-8; the <c>Preamble</c> of <see cref="StrictUtf8"/> is empty.</summary>
    private static ReadOnlySpan<byte> ByteOrderMark => [0xEF, 0xBB, 0xBF];

    /// <summary>The keys typed up to Enter, without echo; Backspace takes the last one back, and other control keys are skipped.</summary>
    public static int ReadKeys(Span<char> buffer)
    {
        var typed = 0;
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                return Math.Min(typed, buffer.Length);
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed > 0 && --typed < buffer.Length)
                {
                    buffer[typed] = '\0';
                }

                continue;
            }

            if (char.IsControl(key.KeyChar))
            {
                continue;
            }

            if (typed < buffer.Length)
            {
                buffer[typed] = key.KeyChar;
            }

            typed++;
        }
    }

    /// <summary>
    /// One line of <paramref name="input"/> as strict UTF-8, read a byte at a time so that no byte of the next line is taken,
    /// without the line feed, a carriage return before it and a byte order mark at its start. A line that has more characters than the buffer gives the
    /// length of the buffer; an input that ends before any byte, and bytes that are not UTF-8, give -1.
    /// </summary>
    public static int ReadLine(Stream input, Span<char> buffer)
    {
        ArgumentNullException.ThrowIfNull(input);

        // A character of UTF-16 is at most 3 bytes of UTF-8; a pair of surrogates is 4 bytes for 2 characters.
        var bytes = GC.AllocateArray<byte>(buffer.Length * 3 + 1, pinned: true);
        try
        {
            var length = 0;
            var tooLong = false;
            var any = false;
            int next;
            while ((next = input.ReadByte()) >= 0)
            {
                any = true;
                if (next == '\n')
                {
                    break;
                }

                if (length < bytes.Length)
                {
                    bytes[length++] = (byte)next;
                }
                else
                {
                    tooLong = true;
                }
            }

            if (!any)
            {
                return -1;
            }

            if (tooLong)
            {
                return buffer.Length;
            }

            if (length > 0 && bytes[length - 1] == '\r')
            {
                length--;
            }

            // Windows PowerShell with $OutputEncoding of UTF-8 puts the byte order mark before what it pipes to an exe, which
            // would make it a part of the first password (M3.5).
            var line = bytes.AsSpan(0, length);
            if (line.StartsWith(ByteOrderMark))
            {
                line = line[ByteOrderMark.Length..];
            }

            try
            {
                return StrictUtf8.GetCharCount(line) > buffer.Length ? buffer.Length : StrictUtf8.GetChars(line, buffer);
            }
            catch (DecoderFallbackException)
            {
                return -1;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

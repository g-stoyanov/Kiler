using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Kiler.Core;
using Kiler.Crypto;

namespace Kiler.Release;

/// <summary>
/// The release tool (M3 step 11, M3.5; docs/release.md). <c>keygen</c> makes the signing key into a file locked with a
/// password (<see cref="SigningKey.Lock"/>) and prints its public key, which goes into Kiler.App. <c>sign</c> signs the
/// version and the SHA-256 of a published Kiler.exe and writes <c>version.json</c> next to it. A password is typed without
/// echo into a pinned buffer of <see cref="char"/>, passed as a span and zeroed after use; it never becomes a
/// <see cref="string"/> and never reaches a line of the tool. The rules are in the kiler-dotnet skill, release.md.
/// </summary>
internal static class ReleaseTool
{
    /// <summary>The ending of a key file, which <c>.gitignore</c> keeps out of git (decided in M3.5).</summary>
    public const string KeyFileEnding = ".kilerkey";

    /// <summary>The largest key file that <c>sign</c> reads: one line of some 400 characters (kiler-crypto, "Formats").</summary>
    public const int MaxKeyFileSize = 4096;

    public const int Done = 0;

    public const int Refused = 1;

    public const int WrongUse = 2;

    /// <summary>
    /// The option that lets a redirected input give the password, for the tests and for a script with a key made for a check
    /// (security review of M3.5). Without it a redirected input is refused: in a terminal whose input is a pipe, such as Git
    /// Bash, the typed password would be shown and kept in the scrollback.
    /// </summary>
    public const string PasswordFromInput = "--password-stdin";

    public const string Usage = """
        The release tool of Kiler (docs/release.md):
          keygen <key file>       makes a new signing key into <key file>, which ends with .kilerkey and is not there yet,
                                  and prints its public key
          sign <exe> <key file>   signs the version and the SHA-256 of <exe> with the key of <key file>,
                                  and writes version.json next to <exe>
        The password of the key is typed in PowerShell or Windows Terminal, and is not shown. The files are on a local disk.
        --password-stdin at the end takes the password from a line of the input instead: for tests, never the real password.
        """;

    public static int Run(string[] args, IToolConsole console)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(console);
        var fromInput = args is [.., PasswordFromInput];
        if (fromInput)
        {
            args = args[..^1];
        }

        if (args is ["keygen", _] or ["sign", _, _] && console.InputRedirected && !fromInput)
        {
            return Refuse(console, $"The input is not a console, so a typed password could be shown: type it in PowerShell or Windows Terminal. A test gives it as a line of the input with {PasswordFromInput}.");
        }

        switch (args)
        {
            case ["keygen", var file]:
                return KeyGen(file, console);
            case ["sign", var exe, var keyFile]:
                return Sign(exe, keyFile, console);
            case ["--help"]:
                console.Out(Usage);
                return Done;
            default:
                console.Error(Usage);
                return WrongUse;
        }
    }

    private static int KeyGen(string file, IToolConsole console)
    {
        if (LocalPath(file, console) is not { } path)
        {
            return Refused;
        }

        // In lower case, as git takes another case of the pattern in .gitignore only while core.ignorecase is true.
        if (!path.EndsWith(KeyFileEnding, StringComparison.Ordinal))
        {
            return Refuse(console, $"The name of a key file ends with {KeyFileEnding}, in lower case, which .gitignore keeps out of git.");
        }

        if (Path.Exists(path))
        {
            return Refuse(console, $"There is already a {path}: keygen never writes over a file.");
        }

        if (Path.GetDirectoryName(path) is not { } folder || !Directory.Exists(folder))
        {
            return Refuse(console, $"There is no folder for {path}.");
        }

        var password = NewPasswordBuffer();
        var again = NewPasswordBuffer();
        try
        {
            var length = console.ReadPassword("The password of the new signing key: ", password);
            if (Invalid(length) is { } invalid)
            {
                return Refuse(console, invalid);
            }

            var againLength = console.ReadPassword("The password again: ", again);
            if (againLength != length || !password.AsSpan(0, length).SequenceEqual(again.AsSpan(0, length)))
            {
                return Refuse(console, "The two passwords differ; nothing was written.");
            }

            using var key = SigningKey.New();
            if (WriteNew(path, Encoding.UTF8.GetBytes(key.Lock(password.AsSpan(0, length)))) is { } error)
            {
                return Refuse(console, error);
            }

            console.Out($"The signing key is in {path}.");
            console.Out($"Public key: {key.PublicKey}");
            console.Out("Keep the file and its password in the password manager, and put the public key into Kiler.App (docs/release.md).");
            return Done;
        }
        finally
        {
            Zero(password);
            Zero(again);
        }
    }

    private static int Sign(string exe, string keyFile, IToolConsole console)
    {
        // An exe on the shared folder is never signed where it is: anyone who writes the folder may have changed its bytes.
        if (LocalPath(exe, console) is not { } exePath || LocalPath(keyFile, console) is not { } keyPath)
        {
            return Refused;
        }

        // One read of the exe gives both the version and the hash (M3.4, security review).
        if (ReadAll(exePath, VersionFile.MaxExeSize, out var bytes) is { } exeError)
        {
            return Refuse(console, exeError);
        }

        if (!ExeVersion.TryRead(bytes, out var version))
        {
            return Refuse(console, $"{exePath} has no version major.minor.patch in its version resource; nothing was written.");
        }

        var sha256 = SHA256.HashData(bytes);
        if (ReadAll(keyPath, MaxKeyFileSize, out var keyBytes) is { } keyError)
        {
            return Refuse(console, keyError);
        }

        var password = NewPasswordBuffer();
        try
        {
            var length = console.ReadPassword("The password of the signing key: ", password);
            if (length < 0)
            {
                return Refuse(console, "No password was given; nothing was written.");
            }

            // A password longer than Password.MaxLength fills the buffer, and TryUnlock gives false for it.
            if (!SigningKey.TryUnlock(KeyFileText(keyBytes), password.AsSpan(0, length), out var key))
            {
                return Refuse(console, "The password does not unlock the key file, or it is not a key file of Kiler; nothing was written.");
            }

            using (key)
            {
                var signature = key.Sign(version, sha256);
                if (!UpdateSignature.Verify(key.PublicKey, version, sha256, signature))
                {
                    return Refuse(console, "The new signature does not verify; nothing was written.");
                }

                var file = new VersionFile(version, sha256, [signature]);
                var target = Path.Combine(Path.GetDirectoryName(exePath)!, VersionFile.Name);
                if (Write(target, file.ToJson()) is { } error)
                {
                    return Refuse(console, error);
                }

                console.Out($"Kiler {version} is signed: {target}");
                console.Out($"SHA-256: {file.Sha256}");
                console.Out($"Public key: {key.PublicKey}");
                return Done;
            }
        }
        finally
        {
            Zero(password);
        }
    }

    /// <summary>Why a typed password cannot lock a signing key, or null.</summary>
    private static string? Invalid(int length) => length switch
    {
        < 0 => "No password was given; nothing was written.",
        < SigningKey.MinPasswordLength => $"A password of the signing key has at least {SigningKey.MinPasswordLength} characters; nothing was written.",
        > Password.MaxLength => $"A password of the signing key has at most {Password.MaxLength} characters; nothing was written.",
        _ => null,
    };

    /// <summary>
    /// The text of a key file. An editor can save it with the byte order mark of UTF-8, which is not white space for
    /// <see cref="string.Trim()"/>, so it is taken off; any other byte that is not UTF-8 makes a text that does not unlock.
    /// </summary>
    private static string KeyFileText(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? bytes.AsSpan(Encoding.UTF8.Preamble.Length) : bytes);

    /// <summary>A pinned buffer one longer than the longest password, so a longer one is seen.</summary>
    private static char[] NewPasswordBuffer() => GC.AllocateArray<char>(Password.MaxLength + 1, pinned: true);

    private static void Zero(char[] buffer) => CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buffer.AsSpan()));

    /// <summary>The full path of a file on a local disk; a path on the network is refused with a line.</summary>
    private static string? LocalPath(string path, IToolConsole console)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            console.Error($"{path} is not a path: {exception.Message}");
            return null;
        }

        if (IsOnNetwork(full))
        {
            console.Error($"{full} is on the network: the tool takes only files on a local disk, as the team can write the shared folder.");
            return null;
        }

        return full;
    }

    /// <summary>
    /// Whether a full path is on the network: a UNC path, a device path of one (<c>\\?\UNC\</c>), or a drive letter of a
    /// network drive. It repeats <c>CopyFiles.IsNetworkPath</c> of Kiler.App, which is internal there.
    /// </summary>
    internal static bool IsOnNetwork(string path)
    {
        path = path.Replace('/', '\\');
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            path = path[4..];
            if (path.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal)
            || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && new DriveInfo(path[..1]).DriveType == DriveType.Network);
    }

    /// <summary>The bytes of a file of at most <paramref name="limit"/> bytes, read whole through one handle that lets no one write it.</summary>
    private static string? ReadAll(string path, int limit, out byte[] bytes)
    {
        bytes = [];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > limit)
            {
                return $"{path} has more than {limit} bytes.";
            }

            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return stream.ReadByte() < 0 ? null : $"{path} grew while it was read.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"{path} cannot be read: {exception.Message}";
        }
    }

    /// <summary>A new file, never one that is there: a failed write deletes what it made.</summary>
    private static string? WriteNew(string path, byte[] content)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"The key file was not written: {exception.Message}";
        }

        try
        {
            using (stream)
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            File.Delete(path);
            return $"The key file was not written: {exception.Message}";
        }
    }

    /// <summary>
    /// <c>version.json</c>, over the one of an earlier release, through a temporary file and a rename, so a crash or a full
    /// disk never leaves a cut file next to the exe (code review of M3.5).
    /// </summary>
    private static string? Write(string path, byte[] content)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            File.Delete(temporary);
            return $"{path} was not written: {exception.Message}";
        }
    }

    private static int Refuse(IToolConsole console, string reason)
    {
        console.Error(reason);
        return Refused;
    }
}

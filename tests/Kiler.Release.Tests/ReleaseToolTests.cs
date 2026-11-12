using System.Security.Cryptography;
using Kiler.Crypto;

namespace Kiler.Release.Tests;

public sealed class ReleaseToolTests : IDisposable
{
    /// <summary>The made-up password of a second key file, for a change of key (M3.8a).</summary>
    private const string SecondPassword = "second demo release password";

    private readonly ToolFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void KeyGenThenSign_WriteAVersionJsonThatVerifiesWithThePrintedPublicKey()
    {
        var keyFile = _folder["release.kilerkey"];
        var keygen = new FakeConsole(ToolFolder.Password, ToolFolder.Password);
        Assert.Equal(ReleaseTool.Done, ReleaseTool.Run(["keygen", keyFile], keygen));
        var publicKey = keygen.PrintedPublicKey;
        Assert.StartsWith("kiler-signing-key.1.", File.ReadAllText(keyFile), StringComparison.Ordinal);

        var exe = _folder.Exe();
        var sign = new FakeConsole(ToolFolder.Password);
        Assert.Equal(ReleaseTool.Done, ReleaseTool.Run(["sign", exe, keyFile], sign));

        var hash = SHA256.HashData(File.ReadAllBytes(exe));
        var (version, sha256, signature) = ToolFolder.ReadVersionJson(_folder["version.json"]);
        Assert.Equal(ToolFolder.ToolVersion.ToString(), version);
        Assert.Equal(Convert.ToHexStringLower(hash), sha256);
        Assert.True(UpdateSignature.Verify(publicKey, ToolFolder.ToolVersion, hash, signature), "The signature does not verify with the printed public key.");
        Assert.Equal(publicKey, sign.PrintedPublicKey);
        Assert.Contains($"SHA-256: {sha256}", sign.Output);
        Assert.Contains(keyFile, Assert.Single(sign.Prompts), StringComparison.Ordinal);
        Assert.False(keygen.All.Contains(ToolFolder.Password, StringComparison.Ordinal) || sign.All.Contains(ToolFolder.Password, StringComparison.Ordinal), "The tool printed the password.");
    }

    [Fact]
    public void Sign_WithTwoKeyFiles_WritesASignatureOfEachInTheirOrder()
    {
        var (oldKey, oldPublicKey) = _folder.KeyFile("old.kilerkey");
        var (newKey, newPublicKey) = _folder.KeyFile("new.kilerkey", SecondPassword);
        var exe = _folder.Exe();

        var console = new FakeConsole(ToolFolder.Password, SecondPassword);
        Assert.Equal(ReleaseTool.Done, ReleaseTool.Run(["sign", exe, oldKey, newKey], console));

        var hash = SHA256.HashData(File.ReadAllBytes(exe));
        var file = ToolFolder.ReadVersionFile(_folder["version.json"]);
        Assert.Equal(2, file.Signatures.Count);
        Assert.True(UpdateSignature.Verify(oldPublicKey, ToolFolder.ToolVersion, hash, file.Signatures[0]), "The first signature does not verify with the key of the first file.");
        Assert.True(UpdateSignature.Verify(newPublicKey, ToolFolder.ToolVersion, hash, file.Signatures[1]), "The second signature does not verify with the key of the second file.");
        Assert.Equal([oldPublicKey, newPublicKey], console.PrintedPublicKeys);
        Assert.Collection(
            console.Prompts,
            prompt => Assert.Contains(oldKey, prompt, StringComparison.Ordinal),
            prompt => Assert.Contains(newKey, prompt, StringComparison.Ordinal));
        Assert.False(console.All.Contains(ToolFolder.Password, StringComparison.Ordinal) || console.All.Contains(SecondPassword, StringComparison.Ordinal), "The tool printed a password.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sign_TheSameKeyTwice_RefusesAndWritesNothing(bool lockedAgain)
    {
        var (keyFile, _) = _folder.KeyFile();
        var second = keyFile;
        var secondPassword = ToolFolder.Password;
        if (lockedAgain)
        {
            // Another file with another password, so only the key itself is the same.
            Assert.True(SigningKey.TryUnlock(File.ReadAllText(keyFile), ToolFolder.Password, out var key));
            using (key)
            {
                second = _folder["again.kilerkey"];
                secondPassword = SecondPassword;
                File.WriteAllText(second, key.Lock(secondPassword));
            }
        }

        var exe = _folder.Exe();
        File.WriteAllText(_folder["version.json"], "earlier");

        var console = new FakeConsole(ToolFolder.Password, secondPassword);
        Assert.Equal(ReleaseTool.Refused, ReleaseTool.Run(["sign", exe, keyFile, second], console));

        Assert.Equal("earlier", File.ReadAllText(_folder["version.json"]));
        Assert.Contains("the same key", Assert.Single(console.Errors), StringComparison.Ordinal);
        Assert.Empty(console.Output);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Sign_WithTwoKeyFilesAndAWrongPasswordOfEither_RefusesAndWritesNothing(int wrong)
    {
        var (oldKey, _) = _folder.KeyFile("old.kilerkey");
        var (newKey, _) = _folder.KeyFile("new.kilerkey", SecondPassword);
        var exe = _folder.Exe();
        File.WriteAllText(_folder["version.json"], "earlier");
        string[] passwords = [ToolFolder.Password, SecondPassword];
        passwords[wrong] = "another release password";

        var console = new FakeConsole(passwords);
        Assert.Equal(ReleaseTool.Refused, ReleaseTool.Run(["sign", exe, oldKey, newKey], console));

        Assert.Equal("earlier", File.ReadAllText(_folder["version.json"]));
        var error = Assert.Single(console.Errors);
        Assert.Contains("does not unlock", error, StringComparison.Ordinal);
        Assert.Contains(wrong == 0 ? oldKey : newKey, error, StringComparison.Ordinal);
        Assert.Equal(wrong + 1, console.Prompts.Count);
        Assert.Empty(console.Output);
    }

    [Fact]
    public void Sign_WithTwoKeyFilesAndNoSecondPassword_RefusesAndWritesNothing()
    {
        var (oldKey, _) = _folder.KeyFile("old.kilerkey");
        var (newKey, _) = _folder.KeyFile("new.kilerkey", SecondPassword);
        var exe = _folder.Exe();

        var console = new FakeConsole(ToolFolder.Password);
        Assert.Equal(ReleaseTool.Refused, ReleaseTool.Run(["sign", exe, oldKey, newKey], console));

        Assert.False(File.Exists(_folder["version.json"]), "A version.json was written.");
        Assert.Contains(newKey, Assert.Single(console.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Sign_TakesTheVersionFromTheBytesOfTheExe()
    {
        var (keyFile, publicKey) = _folder.KeyFile();
        var version = ToolFolder.ToolVersion;
        var text = version.ToString();
        var changed = text[..^1] + (text[^1] == '7' ? '8' : '7');
        var exe = _folder.Exe(bytes => PeBytes.ReplaceProductVersion(bytes, text.Length - 1, changed[^1]));

        Assert.Equal(ReleaseTool.Done, ReleaseTool.Run(["sign", exe, keyFile], new FakeConsole(ToolFolder.Password)));

        Assert.True(ReleaseVersion.TryParse(changed, out var expected));
        var (written, _, signature) = ToolFolder.ReadVersionJson(_folder["version.json"]);
        Assert.Equal(changed, written);
        Assert.True(UpdateSignature.Verify(publicKey, expected, SHA256.HashData(File.ReadAllBytes(exe)), signature), "The signature is not of the version in the exe.");
    }

    [Fact]
    public void Sign_WritesOverTheVersionJsonOfAnEarlierRelease()
    {
        var (keyFile, publicKey) = _folder.KeyFile();
        var exe = _folder.Exe();
        File.WriteAllText(_folder["version.json"], "{}");

        Assert.Equal(ReleaseTool.Done, ReleaseTool.Run(["sign", exe, keyFile], new FakeConsole(ToolFolder.Password)));

        var (_, _, signature) = ToolFolder.ReadVersionJson(_folder["version.json"]);
        Assert.True(UpdateSignature.Verify(publicKey, ToolFolder.ToolVersion, SHA256.HashData(File.ReadAllBytes(exe)), signature), "The new version.json does not verify.");
        Assert.Equal(["Kiler.exe", "demo.kilerkey", "version.json"], Directory.GetFiles(_folder.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Sign_WithAWrongPassword_RefusesAndWritesNothing()
    {
        var (keyFile, _) = _folder.KeyFile();
        var exe = _folder.Exe();
        File.WriteAllText(_folder["version.json"], "earlier");

        var console = new FakeConsole("another release password");
        Assert.Equal(ReleaseTool.Refused, ReleaseTool.Run(["sign", exe, keyFile], console));

        Assert.Equal("earlier", File.ReadAllText(_folder["version.json"]));
        Assert.Contains("does not unlock", Assert.Single(console.Errors), StringComparison.Ordinal);
        Assert.Empty(console.Output);
    }

    [Fact]
    public void Sign_WithNoPassword_RefusesAndWritesNothing()
    {
        var (keyFile, _) = _folder.KeyFile();
        var exe = _folder.Exe();

        Assert.Equal(ReleaseTool.Refused, ReleaseTool.Run(["sign", exe, keyFile], new FakeConsole()));

        Assert.False(File.Exists(_folder["version.json"]), "A version.json was written.");
    }

    [Fact]
    public void Sign_WithAPasswordLongerThanTheLimit_RefusesAndWritesNothing()
    {
        var (keyFile, _) = _folder.KeyFile();
        var exe = _folder.Exe();

        var console = new FakeConsole(ToolFolder.Password + new string('x', 1024));
        Assert.Equal(ReleaseTool.Refused, ReleaseTool.Run(["sign", exe, keyFile], console));

        Assert.False(File.Exists(_folder["version.json"]), "A version.json was written.");
    }

    [Fact]
    public void Sign_AKeyFileWithTheByteOrderMarkOfUtf8_Unlocks()
    {
        var (keyFile, publicKey) = _folder.KeyFile();
        File.WriteAllText(keyFile, File.ReadAllText(keyFile) + "\r\n", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var exe = _folder.Exe();

        var console = new FakeConsole(ToolFolder.Password);
        Assert.Equal(ReleaseTool.Done, ReleaseTool.Run(["sign", exe, keyFile], console));

        Assert.Equal(publicKey, console.PrintedPublicKey);
    }

    public static TheoryData<string, Action<byte[]>> ExesWithoutAVersion => new()
    {
        { "not a PE", bytes => bytes.AsSpan().Fill((byte)'k') },
        { "no resource directory", PeBytes.RemoveResourceDirectory },
        { "a product version of four parts", bytes => PeBytes.ReplaceProductVersion(bytes, ToolFolder.ToolVersion.ToString().Length, '.') },
        { "a product version that is not a number", bytes => PeBytes.ReplaceProductVersion(bytes, 1, 'x') },
    };

    [Theory]
    [MemberData(nameof(ExesWithoutAVersion))]
    public void Sign_AnExeWithoutAVersion_RefusesBeforeThePasswordAndWritesNothing(string what, Action<byte[]> change)
    {
        var (keyFile, _) = _folder.KeyFile();
        var exe = _folder.Exe(change);

        var console = new FakeConsole(ToolFolder.Password);
        Assert.Equal(ReleaseTool.Refused, ReleaseTool.Run(["sign", exe, keyFile], console));

        Assert.False(File.Exists(_folder["version.json"]), $"A version.json was written for {what}.");
        Assert.Empty(console.Prompts);
        Assert.Contains("has no version", Assert.Single(console.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Sign_AKeyFileOverTheLimit_RefusesBeforeThePassword()
    {
        var (keyFile, _) = _folder.KeyFile();
        File.AppendAllText(keyFile, new string(' ', ReleaseTool.MaxKeyFileSize));
        var exe = _folder.Exe();

        var console = new FakeConsole(ToolFolder.Password);
        Assert.Equal(ReleaseTool.Refused, ReleaseTool.Run(["sign", exe, keyFile], console));

        Assert.Empty(console.Prompts);
        Assert.False(File.Exists(_folder["version.json"]), "A version.json was written.");
    }

    [Fact]
    public void Sign_AMissingExeOrKeyFile_Refuses()
    {
        var (keyFile, _) = _folder.KeyFile();
        var exe = _folder.Exe();

        var noExe = new FakeConsole(ToolFolder.Password);
        Assert.Equal(ReleaseTool.Refused, ReleaseTool.Run(["sign", _folder["Other.exe"], keyFile], noExe));
        var noKey = new FakeConsole(ToolFolder.Password);
        Assert.Equal(ReleaseTool.Refused, ReleaseTool.Run(["sign", exe, _folder["other.kilerkey"]], noKey));
        var noSecondKey = new FakeConsole(ToolFolder.Password, ToolFolder.Password);
        Assert.Equal(ReleaseTool.Refused, ReleaseTool.Run(["sign", exe, keyFile, _folder["other.kilerkey"]], noSecondKey));

        Assert.Empty(noExe.Prompts);
        Assert.Empty(noKey.Prompts);
        Assert.Empty(noSecondKey.Prompts);
        Assert.False(File.Exists(_folder["version.json"]), "A version.json was written.");
    }

    [Fact]
    public void KeyGen_NeverWritesOverAFile()
    {
        var keyFile = _folder["release.kilerkey"];
        File.WriteAllText(keyFile, "an earlier key");

        var console = new FakeConsole(ToolFolder.Password, ToolFolder.Password);
        Assert.Equal(ReleaseTool.Refused, ReleaseTool.Run(["keygen", keyFile], console));

        Assert.Equal("an earlier key", File.ReadAllText(keyFile));
        Assert.Empty(console.Prompts);
        Assert.Empty(console.Output);
    }

    [Theory]
    [InlineData(ToolFolder.Password + "!")]
    [InlineData("demo release passwore")]
    public void KeyGen_WithTwoPasswordsThatDiffer_WritesNothing(string again)
    {
        var keyFile = _folder["release.kilerkey"];

        var console = new FakeConsole(ToolFolder.Password, again);
        Assert.Equal(ReleaseTool.Refused, ReleaseTool.Run(["keygen", keyFile], console));

        Assert.False(File.Exists(keyFile), "A key file was written.");
        Assert.Equal(2, console.Prompts.Count);
        Assert.Empty(console.Output);
    }

    [Theory]
    [InlineData(SigningKey.MinPasswordLength - 1)]
    [InlineData(1025)]
    [InlineData(0)]
    public void KeyGen_WithAPasswordOfAnotherLength_RefusesBeforeTheSecondAndWritesNothing(int length)
    {
        var keyFile = _folder["release.kilerkey"];
        var password = new string('p', length);

        var console = new FakeConsole(password, password);
        Assert.Equal(ReleaseTool.Refused, ReleaseTool.Run(["keygen", keyFile], console));

        Assert.False(File.Exists(keyFile), "A key file was written.");
        Assert.Single(console.Prompts);
    }

    [Fact]
    public void KeyGen_TakesAPasswordOfTheMinimumAndMaximumLength()
    {
        foreach (var length in new[] { SigningKey.MinPasswordLength, 1024 })
        {
            var keyFile = _folder[$"{length}.kilerkey"];
            var password = new string('p', length);
            Assert.Equal(ReleaseTool.Done, ReleaseTool.Run(["keygen", keyFile], new FakeConsole(password, password)));
            Assert.True(SigningKey.TryUnlock(File.ReadAllText(keyFile), password, out var key), $"The key of a password of {length} characters does not unlock.");
            key.Dispose();
        }
    }

    [Fact]
    public void KeyGen_WithNoPassword_RefusesAndWritesNothing()
    {
        var keyFile = _folder["release.kilerkey"];

        Assert.Equal(ReleaseTool.Refused, ReleaseTool.Run(["keygen", keyFile], new FakeConsole()));

        Assert.False(File.Exists(keyFile), "A key file was written.");
    }

    [Theory]
    [InlineData("release.key")]
    [InlineData("release.kilerkey.txt")]
    [InlineData("release.KILERKEY")]
    [InlineData("release")]
    public void KeyGen_AFileOfAnotherEnding_RefusesBeforeThePassword(string name)
    {
        var console = new FakeConsole(ToolFolder.Password, ToolFolder.Password);
        Assert.Equal(ReleaseTool.Refused, ReleaseTool.Run(["keygen", _folder[name]], console));

        Assert.False(File.Exists(_folder[name]), "A key file was written.");
        Assert.Empty(console.Prompts);
    }

    [Fact]
    public void KeyGen_InAFolderThatIsNotThere_RefusesBeforeThePassword()
    {
        var console = new FakeConsole(ToolFolder.Password, ToolFolder.Password);
        Assert.Equal(ReleaseTool.Refused, ReleaseTool.Run(["keygen", Path.Combine(_folder.Path, "none", "release.kilerkey")], console));

        Assert.Empty(console.Prompts);
    }

    [Theory]
    [InlineData("keygen", "release.kilerkey")]
    [InlineData("sign", "Kiler.exe", "demo.kilerkey")]
    [InlineData("sign", "Kiler.exe", "demo.kilerkey", "new.kilerkey")]
    public void Run_WithARedirectedInputAndNoOption_RefusesBeforeThePassword(string command, params string[] names)
    {
        var (keyFile, _) = _folder.KeyFile();
        var exe = _folder.Exe();
        string[] args = [command, .. names.Select(name => _folder[name])];

        var console = new FakeConsole(ToolFolder.Password, ToolFolder.Password) { InputRedirected = true };
        Assert.Equal(ReleaseTool.Refused, ReleaseTool.Run(args, console));

        Assert.Empty(console.Prompts);
        Assert.Contains(ReleaseTool.PasswordFromInput, Assert.Single(console.Errors), StringComparison.Ordinal);
        Assert.False(File.Exists(_folder["release.kilerkey"]) || File.Exists(_folder["version.json"]), "A file was written.");
        Assert.True(File.Exists(keyFile) && File.Exists(exe));
    }

    [Fact]
    public void Run_WithARedirectedInputAndTheOption_ReadsThePassword()
    {
        var keyFile = _folder["release.kilerkey"];
        var console = new FakeConsole(ToolFolder.Password, ToolFolder.Password) { InputRedirected = true };

        Assert.Equal(ReleaseTool.Done, ReleaseTool.Run(["keygen", keyFile, ReleaseTool.PasswordFromInput], console));

        Assert.True(File.Exists(keyFile), "No key file was written.");
    }

    [Theory]
    [InlineData(@"\\server\share\Kiler\Kiler.exe", "local", null)]
    [InlineData(@"\\?\UNC\server\share\Kiler\Kiler.exe", "local", null)]
    [InlineData("local", @"\\server\share\Kiler\demo.kilerkey", null)]
    [InlineData("local", "local", @"\\server\share\Kiler\new.kilerkey")]
    public void Sign_AFileOnTheNetwork_RefusesBeforeThePassword(string exe, string keyFile, string? secondKeyFile)
    {
        var (localKey, _) = _folder.KeyFile();
        var localExe = _folder.Exe();
        string[] args = ["sign", exe == "local" ? localExe : exe, keyFile == "local" ? localKey : keyFile, .. secondKeyFile is null ? [] : new[] { secondKeyFile }];

        var console = new FakeConsole(ToolFolder.Password, ToolFolder.Password);
        Assert.Equal(ReleaseTool.Refused, ReleaseTool.Run(args, console));

        Assert.Empty(console.Prompts);
        Assert.Contains("on the network", Assert.Single(console.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void KeyGen_IntoAFolderOnTheNetwork_RefusesBeforeThePassword()
    {
        var console = new FakeConsole(ToolFolder.Password, ToolFolder.Password);
        Assert.Equal(ReleaseTool.Refused, ReleaseTool.Run(["keygen", @"\\server\share\Kiler\release.kilerkey"], console));

        Assert.Empty(console.Prompts);
        Assert.Contains("on the network", Assert.Single(console.Errors), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"\\server\share\Kiler.exe", true)]
    [InlineData(@"//server/share/Kiler.exe", true)]
    [InlineData(@"\\?\UNC\server\share\Kiler.exe", true)]
    [InlineData(@"\\.\UNC\server\share\Kiler.exe", true)]
    [InlineData(@"\\?\C:\Release\Kiler.exe", false)]
    [InlineData(@"C:\Release\Kiler.exe", false)]
    public void IsOnNetwork_TellsAUncPathFromALocalOne(string path, bool expected)
    {
        Assert.Equal(expected, ReleaseTool.IsOnNetwork(path));
    }

    [Theory]
    [InlineData]
    [InlineData("keygen")]
    [InlineData("sign", "Kiler.exe")]
    [InlineData("keygen", "a.kilerkey", "b.kilerkey")]
    [InlineData("sign", "Kiler.exe", "a.kilerkey", "b.kilerkey", "c.kilerkey")]
    [InlineData("verify", "Kiler.exe")]
    public void Run_OfAnotherForm_ShowsTheUsage(params string[] args)
    {
        var console = new FakeConsole(ToolFolder.Password);
        Assert.Equal(ReleaseTool.WrongUse, ReleaseTool.Run(args, console));

        Assert.Equal(ReleaseTool.Usage, Assert.Single(console.Errors));
        Assert.Empty(console.Prompts);
    }

    [Fact]
    public void Run_Help_ShowsTheUsage()
    {
        var console = new FakeConsole();
        Assert.Equal(ReleaseTool.Done, ReleaseTool.Run(["--help"], console));

        Assert.Equal(ReleaseTool.Usage, Assert.Single(console.Output));
    }
}

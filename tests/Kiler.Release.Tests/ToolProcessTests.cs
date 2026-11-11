using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Kiler.Crypto;

namespace Kiler.Release.Tests;

/// <summary>Kiler.Release.exe itself, with its input redirected: the password is a line of the input (M3.5).</summary>
public sealed class ToolProcessTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private readonly ToolFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task TheExe_MakesAKeyAndSignsWithThePasswordsOfItsInput()
    {
        var keyFile = _folder["release.kilerkey"];
        var keygen = await Run($"{ToolFolder.Password}\n{ToolFolder.Password}\n", "keygen", keyFile, ReleaseTool.PasswordFromInput);
        Assert.Equal(ReleaseTool.Done, keygen.ExitCode);
        var publicKey = PublicKeyOf(keygen.Output);

        var exe = _folder.Exe();
        var sign = await Run($"{ToolFolder.Password}\r\n", "sign", exe, keyFile, ReleaseTool.PasswordFromInput);
        Assert.Equal(ReleaseTool.Done, sign.ExitCode);

        var hash = SHA256.HashData(File.ReadAllBytes(exe));
        var (_, _, signature) = ToolFolder.ReadVersionJson(_folder["version.json"]);
        Assert.True(UpdateSignature.Verify(publicKey, ToolFolder.ToolVersion, hash, signature), "The signature does not verify with the printed public key.");
        Assert.Equal(publicKey, PublicKeyOf(sign.Output));
        Assert.False((keygen.Output + keygen.Errors + sign.Output + sign.Errors).Contains(ToolFolder.Password, StringComparison.Ordinal), "The tool printed the password.");
    }

    [Fact]
    public async Task TheExe_WithAWrongPassword_EndsWithOneAndWritesNothing()
    {
        var (keyFile, _) = _folder.KeyFile();
        var exe = _folder.Exe();

        var sign = await Run("another release password\n", "sign", exe, keyFile, ReleaseTool.PasswordFromInput);

        Assert.Equal(ReleaseTool.Refused, sign.ExitCode);
        Assert.Equal(string.Empty, sign.Output);
        Assert.False(File.Exists(_folder["version.json"]), "A version.json was written.");
    }

    [Fact]
    public async Task TheExe_WithItsInputRedirectedAndNoOption_RefusesAndWritesNothing()
    {
        var (keyFile, _) = _folder.KeyFile();
        var exe = _folder.Exe();

        var sign = await Run($"{ToolFolder.Password}\n", "sign", exe, keyFile);

        Assert.Equal(ReleaseTool.Refused, sign.ExitCode);
        Assert.Contains(ReleaseTool.PasswordFromInput, sign.Errors, StringComparison.Ordinal);
        Assert.False(File.Exists(_folder["version.json"]), "A version.json was written.");
    }

    private static string PublicKeyOf(string output) =>
        Assert.Single(output.Split('\n'), line => line.StartsWith("Public key: ", StringComparison.Ordinal))["Public key: ".Length..].TrimEnd('\r');

    private static async Task<(int ExitCode, string Output, string Errors)> Run(string input, params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Kiler.Release.exe"))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(Limit);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output, await errors);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }
}

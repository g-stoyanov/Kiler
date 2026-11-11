using System.Reflection;
using System.Text.Json;
using Kiler.Crypto;

namespace Kiler.Release.Tests;

/// <summary>A temporary folder of a test, deleted at the end, and what the tests of the tool share.</summary>
internal sealed class ToolFolder : IDisposable
{
    /// <summary>A made-up password of a signing key, public as the source is: no key of these tests signs a release.</summary>
    public const string Password = "demo release password";

    public ToolFolder() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Kiler.Release.Tests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A PE with a version resource, as the compiler makes it: the tool's own assembly, whose product version is the
    /// informational version of the build (1.0.0+&lt;commit&gt;).
    /// </summary>
    public static string ToolAssembly => typeof(ReleaseTool).Assembly.Location;

    /// <summary>The version of the tool's assembly as Kiler reads its own: the informational version without the build metadata.</summary>
    public static ReleaseVersion ToolVersion
    {
        get
        {
            var informational = typeof(ReleaseTool).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            Assert.True(ReleaseVersion.TryParse(plus < 0 ? informational : informational[..plus], out var version), informational);
            return version;
        }
    }

    public string this[string name] => System.IO.Path.Combine(Path, name);

    /// <summary>A copy of <see cref="ToolAssembly"/> in the folder as <c>Kiler.exe</c>, with its bytes changed by <paramref name="change"/>.</summary>
    public string Exe(Action<byte[]>? change = null)
    {
        var bytes = File.ReadAllBytes(ToolAssembly);
        change?.Invoke(bytes);
        var path = this["Kiler.exe"];
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>A key file of <see cref="Password"/>, made by Kiler.Crypto, and its public key.</summary>
    public (string Path, string PublicKey) KeyFile(string name = "demo.kilerkey")
    {
        using var key = SigningKey.New();
        var path = this[name];
        File.WriteAllText(path, key.Lock(Password));
        return (path, key.PublicKey);
    }

    /// <summary>The members of a <c>version.json</c>, as a local copy would read them.</summary>
    public static (string Version, string Sha256, string Signature) ReadVersionJson(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = json.RootElement;
        Assert.Equal(["version", "sha256", "signature"], root.EnumerateObject().Select(member => member.Name));
        return (root.GetProperty("version").GetString()!, root.GetProperty("sha256").GetString()!, root.GetProperty("signature").GetString()!);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

/// <summary>The console of the tests: the passwords in the order they are asked, and the lines of the tool.</summary>
internal sealed class FakeConsole(params string[] passwords) : IToolConsole
{
    private readonly Queue<string> _passwords = new(passwords);

    public List<string> Output { get; } = [];

    public List<string> Errors { get; } = [];

    public List<string> Prompts { get; } = [];

    /// <summary>Every line that the tool wrote, to check that no password is there.</summary>
    public string All => string.Join('\n', Prompts.Concat(Output).Concat(Errors));

    public void Out(string line) => Output.Add(line);

    public void Error(string line) => Errors.Add(line);

    public bool InputRedirected { get; init; }

    public int ReadPassword(string prompt, Span<char> buffer)
    {
        Prompts.Add(prompt);
        if (!_passwords.TryDequeue(out var password))
        {
            return -1;
        }

        var length = Math.Min(password.Length, buffer.Length);
        password.AsSpan(0, length).CopyTo(buffer);
        return length;
    }

    /// <summary>The value after <c>Public key: </c> in the output.</summary>
    public string PrintedPublicKey => Assert.Single(Output, line => line.StartsWith("Public key: ", StringComparison.Ordinal))["Public key: ".Length..];
}

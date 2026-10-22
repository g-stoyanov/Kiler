using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;

namespace Kiler.App.Tests;

public sealed class InstanceFileTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Write_ThenTryRead_GivesThePortAndTheToken()
    {
        var file = new InstanceFile(Path.Combine(_folder.Path, "instance.json"));
        var instance = new RunningInstance(51234, new AccessToken());

        file.Write(instance);
        var read = file.TryRead();

        Assert.NotNull(read);
        Assert.Equal(51234, read.Port);
        Assert.Equal(instance.Token.Value, read.Token.Value);
    }

    [Fact]
    public void Write_HoldsOnlyThePortAndTheToken()
    {
        var path = Path.Combine(_folder.Path, "instance.json");
        var token = new AccessToken();

        new InstanceFile(path).Write(new RunningInstance(51234, token));

        Assert.Equal($$"""{"port":51234,"token":"{{token.Value}}"}""", File.ReadAllText(path));
    }

    [Fact]
    public void Write_CreatesTheFolder_AndLeavesNoOtherFile()
    {
        var folder = Path.Combine(_folder.Path, "Kiler");
        var file = new InstanceFile(Path.Combine(folder, "instance.json"));

        file.Write(new RunningInstance(51234, new AccessToken()));

        Assert.Equal([Path.Combine(folder, "instance.json")], Directory.GetFiles(folder));
    }

    [Fact]
    public void Write_ReplacesTheFileOfAnotherInstance()
    {
        var file = new InstanceFile(Path.Combine(_folder.Path, "instance.json"));
        var newer = new RunningInstance(51235, new AccessToken());

        file.Write(new RunningInstance(51234, new AccessToken()));
        file.Write(newer);

        Assert.Equal(newer.Token.Value, file.TryRead()?.Token.Value);
        Assert.Single(Directory.GetFiles(_folder.Path));
    }

    [Fact]
    public void TryRead_WithoutTheFileOrItsFolder_IsNull()
    {
        Assert.Null(new InstanceFile(Path.Combine(_folder.Path, "instance.json")).TryRead());
        Assert.Null(new InstanceFile(Path.Combine(_folder.Path, "missing", "instance.json")).TryRead());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"port":51234}""")]
    [InlineData("""{"token":"{token}"}""")]
    [InlineData("""{"port":0,"token":"{token}"}""")]
    [InlineData("""{"port":-1,"token":"{token}"}""")]
    [InlineData("""{"port":65536,"token":"{token}"}""")]
    [InlineData("""{"port":51234,"token":null}""")]
    [InlineData("""{"port":51234,"token":"short"}""")]
    [InlineData("""{"port":51234,"token":"{token} "}""")]
    [InlineData("""{"port":51234,"token":"{token}\"x"}""")]
    [InlineData("""{"port":51234,"token":"{token}"}trailing""")]
    public void TryRead_AFileThatIsNotValid_IsNull(string content)
    {
        var path = Path.Combine(_folder.Path, "instance.json");
        File.WriteAllText(path, content.Replace("{token}", new AccessToken().Value, StringComparison.Ordinal));

        Assert.Null(new InstanceFile(path).TryRead());
    }

    [Fact]
    public void TryRead_AFileThatIsOpenForWriting_IsNull()
    {
        var path = Path.Combine(_folder.Path, "instance.json");
        var file = new InstanceFile(path);
        file.Write(new RunningInstance(51234, new AccessToken()));

        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Null(file.TryRead());
    }

    [Fact]
    public void Write_WhenTheRenameFails_ThrowsAndLeavesOnlyTheOldFile()
    {
        var path = Path.Combine(_folder.Path, "instance.json");
        var file = new InstanceFile(path);
        var old = new RunningInstance(51234, new AccessToken());
        file.Write(old);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Record.Exception(() => file.Write(new RunningInstance(51235, new AccessToken())));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }

        Assert.Equal([path], Directory.GetFiles(_folder.Path));
        Assert.Equal(old.Token.Value, file.TryRead()?.Token.Value);
    }

    [Fact]
    public void TryDelete_RemovesTheFile_AndAMissingFileCountsAsGone()
    {
        var path = Path.Combine(_folder.Path, "instance.json");
        var file = new InstanceFile(path);
        file.Write(new RunningInstance(51234, new AccessToken()));

        Assert.True(file.TryDelete());
        Assert.True(file.TryDelete());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TryDelete_WhenTheFolderDoesNotExistYet_CountsAsGone()
    {
        // The first start on a machine: %LOCALAPPDATA%\Kiler is made only by the first write.
        Assert.True(new InstanceFile(Path.Combine(_folder.Path, "Kiler", "instance.json")).TryDelete());
    }

    [Fact]
    public void TryDelete_AFileThatAnotherProgramHoldsWithoutSharingTheDelete_IsFalse()
    {
        var path = Path.Combine(_folder.Path, "instance.json");
        var file = new InstanceFile(path);
        file.Write(new RunningInstance(51234, new AccessToken()));

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(file.TryDelete());
        }

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void WriteUntilStopping_WritesTheFileAndDeletesItWhenStopping()
    {
        var path = Path.Combine(_folder.Path, "instance.json");
        using var stopping = new CancellationTokenSource();

        new InstanceFile(path).WriteUntilStopping(new RunningInstance(51234, new AccessToken()), stopping.Token);
        Assert.True(File.Exists(path));

        stopping.Cancel();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void WriteUntilStopping_AfterTheStopHasBegun_LeavesNoFile()
    {
        // The heartbeat can stop the host right after the start, before Program writes the file.
        var path = Path.Combine(_folder.Path, "instance.json");
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();

        new InstanceFile(path).WriteUntilStopping(new RunningInstance(51234, new AccessToken()), stopping.Token);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task WriteUntilStopping_OnTheRealHost_DeletesTheFileWhileTheServerStillListens()
    {
        var path = Path.Combine(_folder.Path, "instance.json");
        await using var app = KilerHost.Build(new StartOptions());
        await app.StartAsync(TestContext.Current.CancellationToken);
        var port = new Uri(Assert.Single(app.Urls)).Port;

        // Registered first, so it runs after the callback of the file: a token runs its callbacks in reverse order.
        bool? goneWhileListening = null;
        app.Lifetime.ApplicationStopping.Register(() => goneWhileListening = !File.Exists(path) && Listens(port));
        new InstanceFile(path).WriteUntilStopping(new RunningInstance(port, app.Services.GetRequiredService<AccessToken>()), app.Lifetime.ApplicationStopping);
        Assert.True(File.Exists(path));

        await app.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(goneWhileListening);
    }

    [Fact]
    public void DeleteIfOwn_TheFileOfThisInstance_DeletesIt()
    {
        var path = Path.Combine(_folder.Path, "instance.json");
        var file = new InstanceFile(path);
        var instance = new RunningInstance(51234, new AccessToken());
        file.Write(instance);

        file.DeleteIfOwn(instance);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void DeleteIfOwn_TheFileOfAnotherInstance_LeavesIt()
    {
        var path = Path.Combine(_folder.Path, "instance.json");
        var file = new InstanceFile(path);
        var instance = new RunningInstance(51234, new AccessToken());

        file.Write(new RunningInstance(51234, new AccessToken()));
        file.DeleteIfOwn(instance);
        Assert.True(File.Exists(path));

        file.Write(new RunningInstance(51235, instance.Token));
        file.DeleteIfOwn(instance);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void RunningInstance_IsOn127001AndItsEntryAddressHasTheToken()
    {
        var instance = new RunningInstance(51234, new AccessToken());

        Assert.Equal(new Uri("http://127.0.0.1:51234/"), instance.Server);
        Assert.Equal(new Uri($"http://127.0.0.1:51234/?t={instance.Token.Value}"), instance.EntryAddress);
    }

    [Fact]
    public void ToString_OfTheInstanceAndOfTheFileContent_DoesNotShowTheToken()
    {
        var token = new AccessToken();

        Assert.DoesNotContain(token.Value, new RunningInstance(51234, token).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(token.Value, new InstanceFileContent(51234, token.Value).ToString(), StringComparison.Ordinal);
    }

    private static bool Listens(int port)
    {
        using var client = new TcpClient();
        try
        {
            client.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

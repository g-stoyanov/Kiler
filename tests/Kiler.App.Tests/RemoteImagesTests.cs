using System.ComponentModel;
using System.Diagnostics;

namespace Kiler.App.Tests;

/// <summary>
/// The policy that forbids DLLs from a network path (M3.8) and the load of the delay-loaded DLLs from System32 after it
/// (M3.8b), with the calls behind fakes: the policy cannot be turned off, so the test process never turns it on. The smoke
/// tests check both in the published exe.
/// </summary>
public sealed class RemoteImagesTests
{
    private const int AccessDenied = 5;
    private const int ModuleNotFound = 126;

    private const string FromNetwork = @"\\server\share\Kiler\Kiler.exe";

    private readonly StringWriter _errors = new();
    private readonly List<string> _calls = [];

    [Theory]
    [InlineData(FromNetwork)]
    [InlineData(@"C:\Tools\Kiler.exe")]
    [InlineData(null)]
    public void Forbid_TheCallsSucceed_TurnsThePolicyOnFirst_LoadsEachDllInOrder_AndSaysNothing(string? exePath)
    {
        Assert.True(RemoteImages.Forbid(TurnOn(0), Load(), exePath, _errors));

        Assert.Equal(["policy", "SspiCli.dll", "NTASN1.dll", "Secur32.dll", "CRYPT32.dll", "ncrypt.dll"], _calls);
        Assert.Empty(_errors.ToString());
    }

    [Theory]
    [InlineData(FromNetwork)]
    [InlineData(@"\\?\UNC\server\share\Kiler\Kiler.exe")]
    public void Forbid_ThePolicyFailsFromANetworkFolder_SaysSoAndStops_WithoutLoading(string exePath)
    {
        Assert.False(RemoteImages.Forbid(TurnOn(AccessDenied), Load(), exePath, _errors));

        Assert.Equal(["policy"], _calls);
        Assert.Equal(
            $"Kiler does not start from the shared folder: it cannot forbid DLLs from a network path. {new Win32Exception(AccessDenied).Message}{Environment.NewLine}",
            _errors.ToString());
    }

    [Theory]
    [InlineData(@"C:\Tools\Kiler.exe")]
    [InlineData(null)]
    public void Forbid_ThePolicyFailsFromALocalFolder_SaysSo_AndStillLoadsTheDlls(string? exePath)
    {
        Assert.True(RemoteImages.Forbid(TurnOn(AccessDenied), Load(), exePath, _errors));

        Assert.Equal(1 + RemoteImages.SystemDlls.Count, _calls.Count);
        Assert.Equal($"Kiler cannot forbid DLLs from a network path: {new Win32Exception(AccessDenied).Message}{Environment.NewLine}", _errors.ToString());
    }

    [Fact]
    public void Forbid_ALoadFailsFromANetworkFolder_SaysWhichAndStops()
    {
        Assert.False(RemoteImages.Forbid(TurnOn(0), Load(failing: "Secur32.dll"), FromNetwork, _errors));

        Assert.Equal(["policy", "SspiCli.dll", "NTASN1.dll", "Secur32.dll"], _calls);
        Assert.Equal(
            $"Kiler does not start from the shared folder: it cannot load Secur32.dll from System32. {new Win32Exception(ModuleNotFound).Message}{Environment.NewLine}",
            _errors.ToString());
    }

    [Theory]
    [InlineData(@"C:\Tools\Kiler.exe")]
    [InlineData(null)]
    public void Forbid_ALoadFailsFromALocalFolder_SaysWhich_AndLoadsTheOthers(string? exePath)
    {
        Assert.True(RemoteImages.Forbid(TurnOn(0), Load(failing: "NTASN1.dll"), exePath, _errors));

        Assert.Equal(1 + RemoteImages.SystemDlls.Count, _calls.Count);
        Assert.Equal($"Kiler cannot load NTASN1.dll from System32: {new Win32Exception(ModuleNotFound).Message}{Environment.NewLine}", _errors.ToString());
    }

    [Fact]
    public void SystemDlls_LoadWhatTheFirstCallsLoadByName_BeforeTheDllsThatLoadIt()
    {
        // ncrypt.dll and Secur32.dll would otherwise load NTASN1.dll and SspiCli.dll by name, from the folder of the exe first.
        var order = RemoteImages.SystemDlls.ToList();

        Assert.True(order.IndexOf("SspiCli.dll") < order.IndexOf("Secur32.dll"));
        Assert.True(order.IndexOf("NTASN1.dll") < order.IndexOf("ncrypt.dll"));
    }

    [Fact]
    public void AreForbidden_TheTestProcess_IsFalse()
    {
        // Nothing turns the policy on in the tests, so this proves that the query reads the policy, and the smoke tests that
        // the published exe turns it on.
        using var self = Process.GetCurrentProcess();

        Assert.False(RemoteImages.AreForbidden(self.SafeHandle));
    }

    private Func<int> TurnOn(int result) => () =>
    {
        _calls.Add("policy");
        return result;
    };

    private Func<string, int> Load(string? failing = null) => name =>
    {
        _calls.Add(name);
        return name == failing ? ModuleNotFound : 0;
    };
}

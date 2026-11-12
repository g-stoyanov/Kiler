using System.ComponentModel;
using System.Diagnostics;

namespace Kiler.App.Tests;

/// <summary>
/// The policy that forbids DLLs from a network path (M3.8), with the call behind a fake: it cannot be turned off, so the
/// test process never turns it on. The smoke tests check it in the published exe.
/// </summary>
public sealed class RemoteImagesTests
{
    private const int AccessDenied = 5;

    private readonly StringWriter _errors = new();

    [Theory]
    [InlineData(@"\\server\share\Kiler\Kiler.exe")]
    [InlineData(@"C:\Tools\Kiler.exe")]
    [InlineData(null)]
    public void Forbid_TheCallSucceeds_GoesOnAndSaysNothing(string? exePath)
    {
        var calls = 0;

        Assert.True(RemoteImages.Forbid(
            () =>
            {
                calls++;
                return 0;
            },
            exePath,
            _errors));

        Assert.Equal(1, calls);
        Assert.Empty(_errors.ToString());
    }

    [Theory]
    [InlineData(@"\\server\share\Kiler\Kiler.exe")]
    [InlineData(@"\\?\UNC\server\share\Kiler\Kiler.exe")]
    public void Forbid_TheCallFailsFromANetworkFolder_SaysSoAndStops(string exePath)
    {
        Assert.False(RemoteImages.Forbid(() => AccessDenied, exePath, _errors));

        Assert.Equal(
            $"Kiler does not start from the shared folder: it cannot forbid DLLs from a network path. {new Win32Exception(AccessDenied).Message}{Environment.NewLine}",
            _errors.ToString());
    }

    [Theory]
    [InlineData(@"C:\Tools\Kiler.exe")]
    [InlineData(null)]
    public void Forbid_TheCallFailsFromALocalFolder_SaysSoAndGoesOn(string? exePath)
    {
        Assert.True(RemoteImages.Forbid(() => AccessDenied, exePath, _errors));

        Assert.Equal($"Kiler cannot forbid DLLs from a network path: {new Win32Exception(AccessDenied).Message}{Environment.NewLine}", _errors.ToString());
    }

    [Fact]
    public void AreForbidden_TheTestProcess_IsFalse()
    {
        // Nothing turns the policy on in the tests, so this proves that the query reads the policy, and the smoke tests that
        // the published exe turns it on.
        using var self = Process.GetCurrentProcess();

        Assert.False(RemoteImages.AreForbidden(self.SafeHandle));
    }
}

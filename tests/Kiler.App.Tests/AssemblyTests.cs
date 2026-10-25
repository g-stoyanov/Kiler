using System.Reflection;
using System.Runtime.InteropServices;

namespace Kiler.App.Tests;

public class AssemblyTests
{
    [Fact]
    public void KilerApp_IsAnExecutableNamedKiler()
    {
        var app = Assembly.Load("Kiler");

        Assert.NotNull(app.EntryPoint);
    }

    // A DLL with the name of a system DLL next to Kiler.exe in the shared folder must not load (Kiler.App.csproj).
    [Fact]
    public void KilerApp_LoadsNativeLibrariesOnlyFromSystem32()
    {
        var app = Assembly.Load("Kiler");

        Assert.Equal(DllImportSearchPath.System32, app.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>()?.Paths);
    }
}

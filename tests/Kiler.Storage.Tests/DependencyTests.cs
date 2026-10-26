using System.Reflection;
using System.Runtime.InteropServices;

namespace Kiler.Storage.Tests;

public class DependencyTests
{
    [Fact]
    public void KilerStorage_ReferencesNoOtherKilerAssembly()
    {
        var kilerReferences = Assembly.Load("Kiler.Storage").GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name == "Kiler" || name?.StartsWith("Kiler.", StringComparison.Ordinal) == true);

        Assert.Empty(kilerReferences);
    }

    // The P/Invokes of the layer (NativeFiles), as those of Kiler.App: a DLL with the name of a system DLL next to Kiler.exe
    // in the shared folder must not load.
    [Fact]
    public void KilerStorage_LoadsNativeLibrariesOnlyFromSystem32()
    {
        var storage = Assembly.Load("Kiler.Storage");

        Assert.Equal(DllImportSearchPath.System32, storage.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>()?.Paths);
    }
}

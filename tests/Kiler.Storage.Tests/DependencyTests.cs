using System.Reflection;

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
}

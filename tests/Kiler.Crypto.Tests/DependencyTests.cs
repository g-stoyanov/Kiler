using System.Reflection;

namespace Kiler.Crypto.Tests;

public class DependencyTests
{
    [Fact]
    public void KilerCrypto_ReferencesNoOtherKilerAssembly()
    {
        var kilerReferences = Assembly.Load("Kiler.Crypto").GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name == "Kiler" || name?.StartsWith("Kiler.", StringComparison.Ordinal) == true);

        Assert.Empty(kilerReferences);
    }
}

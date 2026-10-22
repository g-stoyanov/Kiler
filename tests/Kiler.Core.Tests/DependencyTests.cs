using System.Reflection;

namespace Kiler.Core.Tests;

public class DependencyTests
{
    [Fact]
    public void KilerCore_ReferencesNoKilerAssemblyButCryptoAndStorage()
    {
        string[] allowed = ["Kiler.Crypto", "Kiler.Storage"];

        var kilerReferences = Assembly.Load("Kiler.Core").GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name == "Kiler" || name?.StartsWith("Kiler.", StringComparison.Ordinal) == true);

        Assert.Empty(kilerReferences.Except(allowed));
    }
}

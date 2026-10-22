using System.Reflection;

namespace Kiler.App.Tests;

public class AssemblyTests
{
    [Fact]
    public void KilerApp_IsAnExecutableNamedKiler()
    {
        var app = Assembly.Load("Kiler");

        Assert.NotNull(app.EntryPoint);
    }
}

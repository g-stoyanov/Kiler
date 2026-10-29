using Kiler.Storage;

namespace Kiler.App.Tests;

public class WindowsAccountTests
{
    [Fact]
    public void WithAccount_ItIsTheOneGiven()
    {
        Assert.Equal("demo.user", new WindowsAccount("demo.user").Name);
    }

    [Fact]
    public void WithoutAccount_ItIsTheWindowsUserOfTheProcess_InLowercase()
    {
        // Environment.UserName has no domain. A name that cannot be an account gives none.
        var lower = Environment.UserName.ToLowerInvariant();

        Assert.Equal(StorageName.IsValid(lower) ? lower : null, new WindowsAccount(null).Name);
    }
}

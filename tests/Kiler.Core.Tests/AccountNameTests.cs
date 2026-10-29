namespace Kiler.Core.Tests;

public class AccountNameTests
{
    [Theory]
    [InlineData("demo.user", "demo.user")]
    [InlineData("Demo.User", "demo.user")]
    [InlineData(@"DEMO\Demo.User", "demo.user")]
    [InlineData(@"demo.example\DEMO_USER-2", "demo_user-2")]
    [InlineData(@"\demo.user", "demo.user")]
    public void AWindowsName_IsTheAccountWithoutTheDomain_InLowercase(string windowsName, string account)
    {
        Assert.Equal(account, AccountName.From(windowsName));
    }

    [Fact]
    public void AName_Of64Characters_IsAnAccount_AndOf65_IsNot()
    {
        Assert.Equal(new string('a', 64), AccountName.From(@"DEMO\" + new string('A', 64)));
        Assert.Null(AccountName.From(new string('a', 65)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"DEMO\")]
    [InlineData("demo user")]
    [InlineData("demo@example")]
    [InlineData("Демо")]
    [InlineData("con")]
    [InlineData("demo..user")]
    [InlineData(".demo")]
    [InlineData("demo.")]
    [InlineData("demo.tmp")]
    [InlineData("demo/user")]
    public void AName_ThatFailsTheNameCheck_IsNoAccount(string windowsName)
    {
        Assert.Null(AccountName.From(windowsName));
    }

    [Fact]
    public void ALetterOutsideAscii_IsNotMadeAnAsciiOne()
    {
        // The Kelvin sign, which the lowercase of .NET makes "k".
        Assert.Null(AccountName.From((char)0x212A + "iler"));
    }
}

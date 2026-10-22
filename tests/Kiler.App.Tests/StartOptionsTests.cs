namespace Kiler.App.Tests;

public class StartOptionsTests
{
    [Fact]
    public void NoArguments_StartsTheServerOnAFreePortWithTheDefaults()
    {
        var options = Parse();

        Assert.Equal(StartMode.Server, options.Mode);
        Assert.False(options.NoBrowser);
        Assert.Null(options.Port);
        Assert.Null(options.DataFolder);
    }

    [Fact]
    public void AllServerOptions_AreRead()
    {
        var options = Parse("--no-browser", "--port", "5050", "--data", @"C:\demo data");

        Assert.Equal(StartMode.Server, options.Mode);
        Assert.True(options.NoBrowser);
        Assert.Equal(5050, options.Port);
        Assert.Equal(@"C:\demo data", options.DataFolder);
    }

    [Fact]
    public void ServerOptions_InAnyOrder_AreRead()
    {
        var options = Parse("--data", "demo", "--no-browser", "--port", "8080");

        Assert.Equal(new StartOptions { NoBrowser = true, Port = 8080, DataFolder = "demo" }, options);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("65535", 65535)]
    public void Port_AtTheLimits_IsAccepted(string value, int expected)
    {
        Assert.Equal(expected, Parse("--port", value).Port);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("99999999999")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("+80")]
    [InlineData(" 80")]
    [InlineData("80 ")]
    [InlineData("8_0")]
    [InlineData("0x50")]
    [InlineData("８０")]
    public void Port_NotANumberFrom1To65535_IsAnError(string value)
    {
        var error = ParseError("--port", value);

        Assert.Contains("--port", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--port")]
    [InlineData("--port", "--no-browser")]
    [InlineData("--data")]
    [InlineData("--data", "--no-browser")]
    [InlineData("--data", "")]
    [InlineData("--data", "   ")]
    [InlineData("--data", "-x")]
    public void ValueOption_WithoutAValue_IsAnError(params string[] args)
    {
        var error = ParseError(args);

        Assert.Contains(args[0], error, StringComparison.Ordinal);
    }

    [Fact]
    public void Diag_WithoutAFolder_UsesTheDataFolder()
    {
        var options = Parse("--diag");

        Assert.Equal(StartMode.Diag, options.Mode);
        Assert.Null(options.DiagFolder);
    }

    [Fact]
    public void Diag_WithAFolder_ReadsTheFolder()
    {
        var options = Parse("--diag", @"\\share\kiler");

        Assert.Equal(StartMode.Diag, options.Mode);
        Assert.Equal(@"\\share\kiler", options.DiagFolder);
    }

    [Fact]
    public void Diag_FollowedByData_TakesDataAsAnOptionNotAsTheDiagFolder()
    {
        var options = Parse("--diag", "--data", "demo");

        Assert.Equal(StartMode.Diag, options.Mode);
        Assert.Null(options.DiagFolder);
        Assert.Equal("demo", options.DataFolder);
    }

    [Theory]
    [InlineData("--diag", "--no-browser")]
    [InlineData("--diag", "--port", "5050")]
    [InlineData("--port", "5050", "--diag", "folder")]
    public void Diag_WithAServerOption_IsAnError(params string[] args)
    {
        var error = ParseError(args);

        Assert.Contains("--diag", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    [InlineData("--no-browser", "--help")]
    public void Help_ShowsTheUsage(params string[] args)
    {
        Assert.Equal(StartMode.Help, Parse(args).Mode);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--Port")]
    [InlineData("--port=5050")]
    [InlineData("-port")]
    [InlineData("folder")]
    [InlineData("")]
    public void UnknownArgument_IsAnErrorThatNamesIt(string arg)
    {
        var error = ParseError(arg);

        Assert.Contains($"Unknown argument: {arg}", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--no-browser", "--no-browser")]
    [InlineData("--port", "5050", "--port", "6060")]
    [InlineData("--data", "a", "--data", "b")]
    [InlineData("--diag", "--diag")]
    public void RepeatedOption_IsAnError(params string[] args)
    {
        var error = ParseError(args);

        Assert.Contains("more than once", error, StringComparison.Ordinal);
    }

    [Fact]
    public void HostSettingsFromWebApplicationFactory_AreSkipped()
    {
        var options = Parse(
            "--environment=Development", @"--contentRoot=C:\src\Kiler.App", "--applicationName=Kiler", "--port", "5050");

        Assert.Equal(new StartOptions { Port = 5050 }, options);
    }

    [Theory]
    [InlineData("--urls=http://0.0.0.0:80")]
    [InlineData("--environment", "Development")]
    [InlineData("--Environment=Development")]
    public void OtherHostSettings_AreErrors(params string[] args)
    {
        Assert.Contains("Unknown argument", ParseError(args), StringComparison.Ordinal);
    }

    private static StartOptions Parse(params string[] args)
    {
        Assert.True(StartOptions.TryParse(args, out var options, out var error), error);
        return options;
    }

    private static string ParseError(params string[] args)
    {
        Assert.False(StartOptions.TryParse(args, out _, out var error));
        return error;
    }
}

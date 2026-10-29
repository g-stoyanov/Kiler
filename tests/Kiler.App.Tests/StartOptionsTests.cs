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
        Assert.Null(options.Account);
        Assert.Null(options.FirstWait);
        Assert.Null(options.IdleWait);
        Assert.Equal(HeartbeatTimes.Default, options.HeartbeatTimes);
    }

    [Fact]
    public void AllServerOptions_AreRead()
    {
        var options = Parse("--no-browser", "--port", "5050", "--data", @"C:\demo data", "--account", "demo.user", "--first-wait", "3", "--idle-wait", "2");

        Assert.Equal(StartMode.Server, options.Mode);
        Assert.True(options.NoBrowser);
        Assert.Equal(5050, options.Port);
        Assert.Equal(@"C:\demo data", options.DataFolder);
        Assert.Equal("demo.user", options.Account);
        Assert.Equal(TimeSpan.FromSeconds(3), options.FirstWait);
        Assert.Equal(TimeSpan.FromSeconds(2), options.IdleWait);
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
    [InlineData("--first-wait", "1", 1)]
    [InlineData("--first-wait", "3600", 3600)]
    [InlineData("--idle-wait", "1", 1)]
    [InlineData("--idle-wait", "3600", 3600)]
    public void Wait_AtTheLimits_IsAccepted(string option, string value, int seconds)
    {
        var options = Parse(option, value);

        Assert.Equal(TimeSpan.FromSeconds(seconds), option == "--first-wait" ? options.FirstWait : options.IdleWait);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3601")]
    [InlineData("99999999999")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData(" 5")]
    [InlineData("1.5")]
    [InlineData("1e2")]
    [InlineData("５")]
    public void Wait_NotWholeSecondsFrom1To3600_IsAnError(string value)
    {
        Assert.Contains("--first-wait", ParseError("--first-wait", value), StringComparison.Ordinal);
        Assert.Contains("--idle-wait", ParseError("--idle-wait", value), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("demo.user", "demo.user")]
    [InlineData("Demo.User", "demo.user")]
    [InlineData(@"DEMO\demo.user", "demo.user")]
    public void Account_IsTheAccountOfTheName_WithoutTheDomain_InLowercase(string value, string account)
    {
        Assert.Equal(account, Parse("--account", value).Account);
    }

    [Theory]
    [InlineData("demo user")]
    [InlineData("Демо")]
    [InlineData("con")]
    [InlineData("demo..user")]
    [InlineData(@"DEMO\")]
    [InlineData("demo.tmp")]
    public void Account_ThatFailsTheNameCheck_IsAnError(string value)
    {
        Assert.Contains("--account", ParseError("--account", value), StringComparison.Ordinal);
    }

    [Fact]
    public void HeartbeatTimes_TakesTheGivenWaitsAndTheDefaultsForTheRest()
    {
        Assert.Equal(
            HeartbeatTimes.Default with { FirstWait = TimeSpan.FromSeconds(3) },
            Parse("--first-wait", "3").HeartbeatTimes);
        Assert.Equal(
            HeartbeatTimes.Default with { IdleWait = TimeSpan.FromSeconds(2) },
            Parse("--idle-wait", "2").HeartbeatTimes);
        Assert.Equal(
            HeartbeatTimes.Default with { PingInterval = TimeSpan.FromMilliseconds(100) },
            new StartOptions { PingInterval = TimeSpan.FromMilliseconds(100) }.HeartbeatTimes);
    }

    [Theory]
    [InlineData("--port")]
    [InlineData("--port", "--no-browser")]
    [InlineData("--data")]
    [InlineData("--data", "--no-browser")]
    [InlineData("--data", "")]
    [InlineData("--data", "   ")]
    [InlineData("--data", "-x")]
    [InlineData("--account")]
    [InlineData("--account", "--no-browser")]
    [InlineData("--account", "")]
    [InlineData("--account", "  ")]
    [InlineData("--first-wait")]
    [InlineData("--first-wait", "--no-browser")]
    [InlineData("--idle-wait")]
    [InlineData("--idle-wait", "")]
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
    [InlineData("--diag", "--account", "demo.user")]
    [InlineData("--port", "5050", "--diag", "folder")]
    [InlineData("--diag", "--first-wait", "3")]
    [InlineData("--idle-wait", "2", "--diag")]
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
    [InlineData("--account", "demo.user", "--account", "demo.admin")]
    [InlineData("--diag", "--diag")]
    [InlineData("--first-wait", "3", "--first-wait", "4")]
    [InlineData("--idle-wait", "2", "--idle-wait", "2")]
    public void RepeatedOption_IsAnError(params string[] args)
    {
        var error = ParseError(args);

        Assert.Contains("more than once", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--urls=http://0.0.0.0:80")]
    [InlineData("--environment=Development")]
    [InlineData(@"--contentRoot=C:\src\Kiler.App")]
    [InlineData("--applicationName=Kiler")]
    [InlineData("--environment", "Development")]
    [InlineData("--Environment=Development")]
    public void HostSettings_AreErrors(params string[] args)
    {
        Assert.Contains("Unknown argument", ParseError(args), StringComparison.Ordinal);
    }

    [Fact]
    public void ToServerArguments_NoOptions_IsEmpty()
    {
        Assert.Empty(Parse().ToServerArguments());
    }

    [Fact]
    public void ToServerArguments_AllServerOptions_ArePassedOn()
    {
        var options = Parse("--idle-wait", "2", "--account", "Demo.User", "--data", @"C:\demo data\", "--first-wait", "3", "--port", "5050", "--no-browser");

        Assert.Equal(
            ["--no-browser", "--port", "5050", "--data", @"C:\demo data\", "--account", "demo.user", "--first-wait", "3", "--idle-wait", "2"],
            options.ToServerArguments());
    }

    [Fact]
    public void ToServerArguments_TheWaits_GiveTheSameWaitsToTheCopy()
    {
        var options = Parse("--first-wait", "3600", "--idle-wait", "1");

        Assert.Equal(options, Parse([.. options.ToServerArguments()]));
    }

    [Fact]
    public void ToServerArguments_ARelativeDataFolder_BecomesAFullPathThatIsTheSameInstance()
    {
        // The copy runs in its own folder, so a relative path would name another folder, and another instance.
        var options = Parse("--data", "demo");

        var passedOn = Parse([.. options.ToServerArguments()]);

        Assert.Equal(Path.Combine(Environment.CurrentDirectory, "demo"), passedOn.DataFolder);
        Assert.Equal(InstancePlace.For(options.DataFolder, @"C:\Local\Kiler", 1), InstancePlace.For(passedOn.DataFolder, @"C:\Local\Kiler", 1));
    }

    [Theory]
    [InlineData("--diag")]
    [InlineData("--help")]
    public void ToServerArguments_NotAServerStart_IsRefused(string mode)
    {
        Assert.Throws<InvalidOperationException>(() => Parse(mode).ToServerArguments());
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

using System.ComponentModel;

namespace Kiler.App.Tests;

public class BrowserTests
{
    private readonly AccessToken _token = new();
    private readonly Uri _entry;
    private readonly StringWriter _output = new();
    private readonly StringWriter _errors = new();

    public BrowserTests() => _entry = new RunningInstance(51234, _token).EntryAddress;

    [Fact]
    public void OpenOrPrint_WithABrowser_OpensTheAddressAndPrintsNothing()
    {
        var browser = new FakeBrowser();

        Browser.OpenOrPrint(browser, _entry, noBrowser: false, _output, _errors);

        Assert.Equal([_entry], browser.Opened);
        Assert.Empty(_output.ToString());
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void OpenOrPrint_WithNoBrowser_PrintsTheAddressAndOpensNothing()
    {
        var browser = new FakeBrowser();

        Browser.OpenOrPrint(browser, _entry, noBrowser: true, _output, _errors);

        Assert.Empty(browser.Opened);
        Assert.Equal($"Open: {_entry}{Environment.NewLine}", _output.ToString());
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void OpenOrPrint_WhenTheBrowserDoesNotOpen_PrintsTheAddressButNotTheErrorMessage()
    {
        // .NET puts the address into the message of this error.
        var browser = new FakeBrowser { Error = new Win32Exception(2, $"An error occurred trying to start process '{_entry}'.") };

        Browser.OpenOrPrint(browser, _entry, noBrowser: false, _output, _errors);

        Assert.Equal($"Open: {_entry}{Environment.NewLine}", _output.ToString());
        Assert.NotEmpty(_errors.ToString());
        Assert.DoesNotContain(_token.Value, _errors.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://127.0.0.1:1/")]
    [InlineData("http://localhost:1/")]
    [InlineData("http://[::1]:1/")]
    [InlineData("file:///C:/Kiler.Tests.missing/x.cmd")]
    public void DefaultBrowser_AnythingButHttpOn127001_IsRefusedBeforeTheShell(string address)
    {
        Assert.Throws<ArgumentException>(() => new DefaultBrowser().Open(new Uri(address)));
    }

    private sealed class FakeBrowser : IBrowser
    {
        public List<Uri> Opened { get; } = [];

        public Exception? Error { get; init; }

        public void Open(Uri address)
        {
            if (Error is not null)
            {
                throw Error;
            }

            Opened.Add(address);
        }
    }
}

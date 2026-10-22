using System.ComponentModel;
using System.Diagnostics;

namespace Kiler.App;

/// <summary>Opens an address in a browser. Tests replace it, so that no browser opens.</summary>
internal interface IBrowser
{
    void Open(Uri address);
}

/// <summary>The default browser of the user, started through the shell, as a click on a link starts it.</summary>
internal sealed class DefaultBrowser : IBrowser
{
    public void Open(Uri address)
    {
        // The shell starts whatever it gets, a program too, so only an address of this server goes to it.
        if (address.Scheme != Uri.UriSchemeHttp || address.Host != "127.0.0.1")
        {
            throw new ArgumentException("Only an http address on 127.0.0.1 is opened.", nameof(address));
        }

        using var process = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
    }
}

/// <summary>How a start gives the user the address with the token.</summary>
internal static class Browser
{
    /// <summary>
    /// Opens the address in the browser. With <c>--no-browser</c>, or when the browser does not open, it prints the address
    /// to <paramref name="output"/> as <c>Open: &lt;address&gt;</c> instead: the token lets anyone in, so it is printed only
    /// when no browser gets it.
    /// </summary>
    public static void OpenOrPrint(IBrowser browser, Uri entryAddress, bool noBrowser, TextWriter output, TextWriter errors)
    {
        if (!noBrowser)
        {
            try
            {
                browser.Open(entryAddress);
                return;
            }
            // The message is not shown, because .NET puts the address, with the token, into it.
            catch (Win32Exception)
            {
                errors.WriteLine("The browser did not open. Open the address below in a browser.");
            }
        }

        output.WriteLine($"Open: {entryAddress}");
    }
}

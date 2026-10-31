using System.Globalization;
using Kiler.App.Tests;
using Microsoft.Playwright;

[assembly: AssemblyFixture(typeof(Kiler.E2E.KilerBrowser))]

namespace Kiler.E2E;

/// <summary>
/// Playwright's Chromium, headless, one for all the tests, and a context of its own for each page that a test opens. The
/// tests run only when <see cref="KilerExe.Variable"/> names the published exe: <c>e2e.ps1</c> sets it, and <c>test.ps1</c>
/// skips them, as it skips the smoke tests. Chromium, not the <c>msedge</c> channel: headless Edge ended the stream of the
/// shared worker a moment after it opened (M4.7).
/// </summary>
public sealed class KilerBrowser : IAsyncLifetime
{
    public const string NoExe = $"Set {KilerExe.Variable} to a published Kiler.exe; e2e.ps1 does.";

    /// <summary>The folder for the traces of the tests that fail; <c>e2e.ps1</c> sets it.</summary>
    public const string TracesVariable = "KILER_E2E_TRACES";

    private const string OpenPrefix = "Open: ";

    /// <summary>
    /// Reports a violation of the Content Security Policy to the test. The script runs before the page's own, through the
    /// browser, so the policy does not stop it.
    /// </summary>
    private const string ViolationScript = """
        document.addEventListener('securitypolicyviolation', (event) =>
          window.kilerViolation(`${event.violatedDirective} ${event.blockedURI} ${event.sourceFile}:${event.lineNumber}`));
        """;

    /// <summary>The longest wait for a start of Kiler: a busy CI machine, an antivirus scan of the new copy.</summary>
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(30);

    /// <summary>The most the end of Kiler may come after its wait, on a busy machine.</summary>
    private static readonly TimeSpan Late = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The first wait of a run (M1.6): the time to open the page after the start. The frame asks <c>/api/health</c> once
    /// its stream connects, which puts the stop off by this wait, so it is also how long a run can go on after its last
    /// page closes; short, as a test that starts Kiler again on the same data folder waits for the stop first (M4.1).
    /// </summary>
    private static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan IdleWait = TimeSpan.FromSeconds(2);

    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public static bool HasExe => KilerExe.Published is not null;

    /// <summary>Starts a run of <paramref name="exe"/> with short waits and then <paramref name="arguments"/>.</summary>
    internal static KilerRun Start(KilerExe exe, params string[] arguments) =>
        exe.Start(["--first-wait", Seconds(FirstWait), "--idle-wait", Seconds(IdleWait), .. arguments]);

    /// <summary>Waits for a run whose pages have closed to stop, and checks that it ended well.</summary>
    internal static async Task WaitForStopAsync(KilerRun run)
    {
        var exitCode = await run.WaitForExitAsync(FirstWait + Late);
        Assert.True(exitCode == 0, $"Kiler.exe ended with exit code {exitCode}.{run.Describe()}");
    }

    private static string Seconds(TimeSpan wait) => ((int)wait.TotalSeconds).ToString(CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync()
    {
        // Without the exe the tests skip, so they need no browser either.
        if (!HasExe)
        {
            return;
        }

        // The headless shell of Chromium, which a headless launch takes, into %LOCALAPPDATA%\ms-playwright when it is not
        // there yet. From code, as the playwright.ps1 of the docs needs PowerShell 7. Through a proxy the download takes
        // HTTPS_PROXY, and NODE_EXTRA_CA_CERTS for a proxy with a root certificate of its own.
        var installed = Microsoft.Playwright.Program.Main(["install", "chromium", "--only-shell"]);
        if (installed != 0)
        {
            throw new InvalidOperationException($"The install of Chromium for Playwright ended with exit code {installed}.");
        }

        // The wizard makes the keys, which takes some seconds on a busy CI machine.
        Assertions.SetDefaultExpectTimeout(15_000);
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
    }

    /// <summary>
    /// Opens the address of the <c>Open:</c> line of <paramref name="run"/> in a new context, as the browser does at a start:
    /// the exchange of the token for the cookie, then the page at <c>/</c>, which must connect its stream through the shared
    /// worker.
    /// </summary>
    internal async Task<KilerPage> OpenAsync(KilerRun run)
    {
        var line = await run.WaitForLineAsync(OpenPrefix, Quick);
        var entry = new Uri(line[OpenPrefix.Length..]);
        var context = await _browser!.NewContextAsync();
        var page = new KilerPage(context, await context.NewPageAsync(), entry.GetLeftPart(UriPartial.Authority) + "/");
        try
        {
            await context.ExposeFunctionAsync("kilerViolation", (string violation) => page.Violations.Add(violation));
            await context.AddInitScriptAsync(ViolationScript);
            await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true });

            IResponse? response;
            try
            {
                response = await page.Page.GotoAsync(entry.AbsoluteUri);
            }
            // The message of Playwright holds the address, with the token, and it goes into the public CI log.
            catch (PlaywrightException ex)
            {
                throw new InvalidOperationException($"The page did not open: {KilerExe.MaskToken(ex.Message)}");
            }

            // The exchange redirects to the root; the routes of the page may then go on, such as to the sign-in.
            Assert.True(response?.Url == page.Root, $"The exchange did not end at the root: {KilerExe.MaskToken(response?.Url ?? "no answer")}");
            await Assertions.Expect(page.Page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Connected" })).ToBeVisibleAsync();
            return page;
        }
        catch
        {
            await page.DisposeAsync();
            throw;
        }
    }
}

/// <summary>
/// A page of Kiler in a context of its own, with a trace. A test ends it with <see cref="CloseAsync"/>, which closes the
/// context, so its stream ends, and fails on a violation of the policy. A page that is disposed without it, as when the
/// test fails on it, and one that broke the policy keep their trace in <see cref="KilerBrowser.TracesVariable"/>.
/// </summary>
public sealed class KilerPage(IBrowserContext context, IPage page, string root) : IAsyncDisposable
{
    private bool _closed;

    public IPage Page { get; } = page;

    /// <summary>The root of the page's Kiler, <c>http://127.0.0.1:&lt;port&gt;/</c>.</summary>
    public string Root { get; } = root;

    /// <summary>The violations of the Content Security Policy that the pages of the context reported.</summary>
    public List<string> Violations { get; } = [];

    /// <summary>Closes the context, so its stream ends, and checks that no page broke the policy.</summary>
    public async Task CloseAsync()
    {
        var broke = Violations.Count > 0;
        await EndAsync(keepTrace: broke);
        Assert.False(broke, $"The page broke the Content Security Policy:{Environment.NewLine}{string.Join(Environment.NewLine, Violations)}");
    }

    public ValueTask DisposeAsync() => EndAsync(keepTrace: true);

    private async ValueTask EndAsync(bool keepTrace)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        try
        {
            await context.Tracing.StopAsync(new() { Path = keepTrace ? TracePath() : null });
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    private static string TracePath()
    {
        var folder = Environment.GetEnvironmentVariable(KilerBrowser.TracesVariable) is { Length: > 0 } set
            ? set
            : Path.Combine(AppContext.BaseDirectory, "traces");
        Directory.CreateDirectory(folder);
        var name = TestContext.Current.TestMethod?.MethodName ?? "test";
        return Path.Combine(folder, $"{name}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.zip");
    }
}

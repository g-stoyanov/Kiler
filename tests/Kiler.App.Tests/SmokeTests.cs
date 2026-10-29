using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace Kiler.App.Tests;

/// <summary>
/// The published Kiler.exe, run as a process: what the API tests cannot see, such as Native AOT, the embedded UI, the
/// console and the exit codes, the single instance across processes. They run only when <see cref="KilerExe.Variable"/>
/// names the exe: <c>smoke.ps1</c> sets it after <c>publish.ps1</c>, and <c>test.ps1</c> skips them. Each test runs a copy
/// of the exe in a temporary folder with a data folder of its own (<see cref="KilerExe"/>), always with <c>--no-browser</c>.
/// </summary>
[Trait("Category", "Smoke")]
public partial class SmokeTests
{
    private const string NoExe = $"Set {KilerExe.Variable} to a published Kiler.exe; smoke.ps1 does.";
    private const string OpenPrefix = "Open: ";
    private const string Stopped = "No Kiler page was open, so Kiler has stopped.";

    /// <summary>The offset of <c>DependentLoadFlags</c> in <c>IMAGE_LOAD_CONFIG_DIRECTORY64</c>.</summary>
    private const int DependentLoadFlagsOffset = 78;

    /// <summary><c>LOAD_LIBRARY_SEARCH_SYSTEM32</c>, from <c>/DEPENDENTLOADFLAG:0x800</c> in Kiler.App.csproj (M1.8).</summary>
    private const ushort LoadLibrarySearchSystem32 = 0x800;

    /// <summary>The longest wait for a start or an end that does not wait on purpose: a busy CI machine, an antivirus scan of the new copy.</summary>
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(30);

    /// <summary>A timer of the exe may fire this much before the clock of the test says.</summary>
    private static readonly TimeSpan Early = TimeSpan.FromMilliseconds(100);

    /// <summary>The most the end of the process may come after its wait, on a busy machine.</summary>
    private static readonly TimeSpan Late = TimeSpan.FromSeconds(10);

    /// <summary>The longest a run of <c>--diag</c> may take on the local disk: some seconds, and an antivirus scans every new file.</summary>
    private static readonly TimeSpan DiagRun = TimeSpan.FromMinutes(2);

    public static bool HasExe => KilerExe.Published is not null;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(Skip = NoExe, SkipUnless = nameof(HasExe))]
    public void Exe_IsForX64_AndLoadsTheDllsItImportsOnlyFromSystem32()
    {
        using var file = File.OpenRead(KilerExe.Published!);
        using var pe = new PEReader(file);

        Assert.Equal(Machine.Amd64, pe.PEHeaders.CoffHeader.Machine);
        Assert.Equal(PEMagic.PE32Plus, pe.PEHeaders.PEHeader!.Magic);
        Assert.Equal(LoadLibrarySearchSystem32, ReadDependentLoadFlags(pe));
    }

    [Fact(Skip = NoExe, SkipUnless = nameof(HasExe))]
    public async Task Start_PrintsTheAddressWithTheToken_ServesThePage_AndStopsAfterThePageCloses()
    {
        using var exe = new KilerExe();
        var port = KilerHostTests.FreePort();
        var elsewhere = KilerHostTests.FreePort();
        // The first wait leaves the test time to open the page; after the health check it is the wait the page must outlast.
        var firstWait = TimeSpan.FromSeconds(3);
        var idleWait = TimeSpan.FromSeconds(1);
        using var run = exe.Start(
            ["--port", Text(port), "--first-wait", Text(firstWait), "--idle-wait", Text(idleWait)], AllInterfaces(elsewhere));

        var entry = ReadEntryAddress(await run.WaitForLineAsync(OpenPrefix, Quick));

        AssertListensOnlyOn127001(port);
        Assert.DoesNotContain(Listeners(), endpoint => endpoint.Port == elsewhere);
        Assert.Equal(port, entry.Port);
        Assert.Contains($"Kiler is listening on http://127.0.0.1:{port}. It stops when its last page closes, or with Ctrl+C.", run.Output);
        var written = new InstanceFile(exe.InstanceFile).TryRead();
        Assert.True(written?.EntryAddress == entry, "The instance file does not name this Kiler.");

        // The exchange, as the browser makes it.
        using var anonymous = CreateClient(entry);
        string cookie;
        using (var exchange = await anonymous.GetAsync(entry.PathAndQuery, Cancel))
        {
            // Assert.Single and Assert.Equal would print the values, and these can hold the token.
            Assert.Equal(HttpStatusCode.Redirect, exchange.StatusCode);
            var location = exchange.Headers.Location?.OriginalString;
            Assert.True(location == "/", $"The exchange redirects to {KilerExe.MaskToken(location ?? "nothing")}.");
            var setCookies = exchange.Headers.GetValues("Set-Cookie").ToArray();
            Assert.True(setCookies.Length == 1, $"The exchange sets {setCookies.Length} cookies.");
            cookie = setCookies[0].Split(';')[0];
            Assert.True(cookie == $"{exe.CookieName}={TokenOf(entry)}", "The cookie is not the token under the name of the instance.");
        }

        using var client = CreateClient(entry, cookie);
        using var page = await EventStream.OpenAsync(CreateClient(entry, cookie));

        var asked = Stopwatch.GetTimestamp();
        using (var health = await client.GetAsync("/api/health", Cancel))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Equal($$"""{"version":"{{HealthApi.Version}}"}""", await health.Content.ReadAsStringAsync(Cancel));
        }

        var html = await AssertUiFileAsync(client, "/", "text/html");
        Assert.Contains("<div id=\"root\"></div>", html, StringComparison.Ordinal);
        var script = await AssertUiFileAsync(client, Assert.Single(PageScript().Matches(html)).Groups[1].Value, "text/javascript");
        // The page's script names the worker; publish.ps1 checks only the file names.
        var worker = $"/assets/{Assert.Single(WorkerScript().Matches(script).Select(match => match.Value).Distinct())}";
        await AssertUiFileAsync(client, worker, "text/javascript");
        foreach (var path in new[] { "/", worker, "/api/health" })
        {
            using var refused = await anonymous.GetAsync(path, Cancel);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        // The health check put the stop off by the first wait. Past it, only the open page keeps Kiler running.
        var left = firstWait + TimeSpan.FromSeconds(1) - Stopwatch.GetElapsedTime(asked);
        if (left > TimeSpan.Zero)
        {
            await Task.Delay(left, Cancel);
        }

        Assert.False(run.HasExited, "Kiler stopped while a page was open.");

        var closed = Stopwatch.GetTimestamp();
        page.Dispose();

        Assert.Equal(0, await run.WaitForExitAsync(idleWait + Late));
        AssertStoppedAfter(closed, idleWait);
        Assert.Equal(Stopped, run.Output[^1]);
        Assert.Empty(run.Errors);
        Assert.False(File.Exists(exe.InstanceFile), "Kiler left its instance file.");
        Assert.DoesNotContain(Listeners(), endpoint => endpoint.Port == port);
    }

    [Fact(Skip = NoExe, SkipUnless = nameof(HasExe))]
    public async Task NoPage_StopsAfterTheFirstWait()
    {
        using var exe = new KilerExe();
        var port = KilerHostTests.FreePort();
        var firstWait = TimeSpan.FromSeconds(2);
        using var run = exe.Start(["--port", Text(port), "--first-wait", Text(firstWait)]);
        await run.WaitForLineAsync(OpenPrefix, Quick);

        // The wait starts before the address is printed, so the time from here bounds it from above.
        Assert.Equal(0, await run.WaitForExitAsync(firstWait + Late));
        AssertStoppedAfter(run.Started, firstWait);
        Assert.Equal(Stopped, run.Output[^1]);
        Assert.Empty(run.Errors);
        Assert.False(File.Exists(exe.InstanceFile), "Kiler left its instance file.");
        Assert.DoesNotContain(Listeners(), endpoint => endpoint.Port == port);
    }

    [Fact(Skip = NoExe, SkipUnless = nameof(HasExe))]
    public async Task SecondStart_PrintsTheAddressOfTheRunningKiler_AndStartsNoServer()
    {
        using var exe = new KilerExe();
        var port = KilerHostTests.FreePort();
        var otherPort = KilerHostTests.FreePort();
        var firstWait = TimeSpan.FromSeconds(5);
        var idleWait = TimeSpan.FromSeconds(1);
        using var first = exe.Start(["--port", Text(port), "--first-wait", Text(firstWait), "--idle-wait", Text(idleWait)]);
        var openLine = await first.WaitForLineAsync(OpenPrefix, Quick);
        var entry = ReadEntryAddress(openLine);
        using var page = await EventStream.OpenAsync(CreateClient(entry, $"{exe.CookieName}={TokenOf(entry)}"));

        using var second = exe.Start(["--port", Text(otherPort), "--first-wait", "7", "--idle-wait", "7"]);

        Assert.Equal(0, await second.WaitForExitAsync(Quick));
        Assert.Equal(
            [
                $"Kiler is already running on http://127.0.0.1:{port}.",
                $"--port {otherPort} is ignored.",
                "--first-wait is ignored.",
                "--idle-wait is ignored.",
                KilerExe.MaskToken(openLine),
            ],
            second.Output);
        Assert.True(await second.WaitForLineAsync(OpenPrefix, Quick) == openLine, "The second start printed another token.");
        Assert.Empty(second.Errors);
        Assert.False(first.HasExited, "The first Kiler stopped.");

        // The health check of the second start put the stop off by the first wait.
        var closed = Stopwatch.GetTimestamp();
        page.Dispose();

        Assert.Equal(0, await first.WaitForExitAsync(firstWait + Late));
        AssertStoppedAfter(closed, idleWait);
        Assert.False(File.Exists(exe.InstanceFile), "Kiler left its instance file.");
    }

    [Fact(Skip = NoExe, SkipUnless = nameof(HasExe))]
    public async Task Start_FillsTheLocalCopyOfItsDataFolderInTheProfile_AndTheTestDoesNotLeaveIt()
    {
        var exe = new KilerExe();
        try
        {
            using var writer = new TempFolder();
            var made = MadeUpData.Create(exe.DataFolder, writer.Path);
            made.Create("a1");
            made.Create("b2");
            var idleWait = TimeSpan.FromSeconds(1);
            using var run = exe.Start(["--first-wait", "30", "--idle-wait", Text(idleWait)]);
            var entry = ReadEntryAddress(await run.WaitForLineAsync(OpenPrefix, Quick));

            // A page keeps Kiler running until its first load has been said, as nothing is said once Kiler stops.
            using (await EventStream.OpenAsync(CreateClient(entry, $"{exe.CookieName}={TokenOf(entry)}")))
            {
                await run.WaitForLineAsync("Loaded the data folder: 2 new or changed, 0 gone, 0 changed outside Kiler, 0 skipped, in ", Quick);
            }

            Assert.StartsWith(UserFolders.Kiler() + Path.DirectorySeparatorChar, exe.CopyFolder, StringComparison.OrdinalIgnoreCase);
            foreach (var id in new[] { "a1", "b2" })
            {
                Assert.Equal(File.ReadAllBytes(made.RecordFile(id)), File.ReadAllBytes(MadeUpData.CopyFile(exe.CopyFolder, id)));
            }

            Assert.Equal(0, await run.WaitForExitAsync(idleWait + Late));
            Assert.Equal(Stopped, run.Output[^1]);
            Assert.Empty(run.Errors);
        }
        finally
        {
            exe.Dispose();
        }

        Assert.False(Directory.Exists(exe.CopyFolder), "The test left the local copy in the profile.");
    }

    [Fact(Skip = NoExe, SkipUnless = nameof(HasExe))]
    public async Task KilledKiler_TheNextStartTakesOver_WithoutAskingTheOldPort()
    {
        using var exe = new KilerExe();
        var oldPort = KilerHostTests.FreePort();
        var newPort = KilerHostTests.FreePort();
        using var killed = exe.Start(["--port", Text(oldPort)]);
        var old = ReadEntryAddress(await killed.WaitForLineAsync(OpenPrefix, Quick));
        // As Task Manager ends it: the mutex is abandoned, and the instance file stays with the old port and token.
        await killed.KillAsync();
        Assert.True(File.Exists(exe.InstanceFile), "The killed Kiler left no instance file.");

        // Whatever listens on the old port now must not get the token.
        var trap = new TcpListener(IPAddress.Loopback, oldPort);
        trap.Start();
        try
        {
            var firstWait = TimeSpan.FromSeconds(2);
            using var next = exe.Start(["--port", Text(newPort), "--first-wait", Text(firstWait)]);
            var entry = ReadEntryAddress(await next.WaitForLineAsync(OpenPrefix, Quick));

            Assert.Equal(newPort, entry.Port);
            Assert.True(TokenOf(entry) != TokenOf(old), "The next start took the token of the killed Kiler.");
            Assert.True(new InstanceFile(exe.InstanceFile).TryRead()?.EntryAddress == entry, "The instance file does not name the next Kiler.");
            Assert.Equal(0, await next.WaitForExitAsync(firstWait + Late));
            Assert.False(File.Exists(exe.InstanceFile), "Kiler left its instance file.");
            Assert.False(trap.Pending(), "The next start connected to the port of the killed Kiler.");
        }
        finally
        {
            trap.Stop();
        }
    }

    [Fact(Skip = NoExe, SkipUnless = nameof(HasExe))]
    public async Task PortInUse_EndsWithExitCode1_AndLeavesNoInstanceFile()
    {
        using var exe = new KilerExe();
        var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        try
        {
            var port = ((IPEndPoint)busy.LocalEndpoint).Port;
            using var run = exe.Start(["--port", Text(port)]);

            Assert.Equal(1, await run.WaitForExitAsync(Quick));
            Assert.StartsWith($"Kiler cannot listen on port {port}: ", Assert.Single(run.Errors), StringComparison.Ordinal);
            Assert.DoesNotContain(run.Output, line => line.StartsWith(OpenPrefix, StringComparison.Ordinal));
            Assert.False(File.Exists(exe.InstanceFile), "Kiler left an instance file.");
        }
        finally
        {
            busy.Stop();
        }
    }

    [Fact(Skip = NoExe, SkipUnless = nameof(HasExe))]
    public async Task Diag_MeasuresTheDataFolder_CleansUp_AndSavesAReportWithoutPathsOrNames()
    {
        using var exe = new KilerExe();
        using var run = exe.Diag(["--data", exe.DataFolder]);

        var exitCode = await run.WaitForExitAsync(DiagRun);

        // A process that runs as administrator may give new files to the Administrators group, and the owner checks fail.
        Assert.True(exitCode == (FileDiagTests.OwnerIsTheUser ? 0 : 1), $"Exit code {exitCode}.{run.Describe()}");
        Assert.Empty(run.Errors);
        var output = run.Output;
        Assert.StartsWith($"Kiler {HealthApi.Version}, --diag, ", output[0], StringComparison.Ordinal);
        Assert.Equal("Exe location: local disk", output[2]);
        Assert.StartsWith("Process start to Main: ", output[3], StringComparison.Ordinal);
        Assert.Equal($"Test folder: {exe.DataFolder} (local disk)", output[4]);
        Assert.Contains(output, line => line.StartsWith($"Write, one by one: {FileDiag.DefaultFileCount} files in ", StringComparison.Ordinal));
        Assert.Contains(output, line => line.StartsWith("[OK] Replace by rename over the old file: ", StringComparison.Ordinal));
        Assert.Contains(FileDiagTests.OwnerLine("Owner of a new file"), output);
        Assert.Equal(FileDiagTests.OwnerIsTheUser ? 0 : 3, output.Count(line => line.StartsWith("[FAIL]", StringComparison.Ordinal)));
        Assert.Equal("[OK] Temporary subfolder removed", output[^2]);
        Assert.Empty(Directory.GetFileSystemEntries(exe.DataFolder));

        var (path, report) = ReadDiagReport(exe);
        Assert.Equal($"Report saved: {path}", output[^1]);
        Assert.Equal(output.SkipLast(1).Select(line => line.Replace(exe.DataFolder, "<test folder>", StringComparison.Ordinal)), Lines(report.TrimEnd()));
        AssertNoPathOrName(report);
    }

    [Fact(Skip = NoExe, SkipUnless = nameof(HasExe))]
    public async Task Diag_AFolderThatDoesNotExist_EndsWithExitCode1_AndStillSavesAReport()
    {
        using var exe = new KilerExe();
        var missing = Path.Combine(exe.DataFolder, "missing");
        using var run = exe.Diag([missing]);

        Assert.Equal(1, await run.WaitForExitAsync(Quick));
        Assert.Equal($"[FAIL] Test folder: it does not exist, so give one that does: Kiler.exe --diag <folder> ({missing})", run.Output[^2]);
        Assert.False(Directory.Exists(missing));

        var (_, report) = ReadDiagReport(exe);
        Assert.Contains("(<test folder>)", report, StringComparison.Ordinal);
        AssertNoPathOrName(report);
    }

    [Fact(Skip = NoExe, SkipUnless = nameof(HasExe))]
    public async Task Help_PrintsTheUsage_AndEndsWithExitCode0()
    {
        using var exe = new KilerExe();
        using var run = exe.Start(["--help"]);

        Assert.Equal(0, await run.WaitForExitAsync(Quick));
        Assert.Equal(Lines(StartOptions.Usage), run.Output);
        Assert.Empty(run.Errors);
    }

    [Fact(Skip = NoExe, SkipUnless = nameof(HasExe))]
    public async Task UnknownArgument_PrintsTheErrorAndTheUsage_AndEndsWithExitCode2()
    {
        using var exe = new KilerExe();
        using var run = exe.Start(["--unknown"]);

        Assert.Equal(2, await run.WaitForExitAsync(Quick));
        Assert.Equal(["Unknown argument: --unknown", "", .. Lines(StartOptions.Usage)], run.Errors);
        Assert.Empty(run.Output);
    }

    private static string Text(int number) => number.ToString(CultureInfo.InvariantCulture);

    private static string Text(TimeSpan wait) => Text((int)wait.TotalSeconds);

    private static string[] Lines(string text) => text.ReplaceLineEndings("\n").Split('\n');

    /// <summary>The one report that <c>--diag</c> saved next to the exe: its path and its text.</summary>
    private static (string Path, string Text) ReadDiagReport(KilerExe exe)
    {
        var path = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(exe.ExePath)!, "Kiler-diag-*.txt"));
        return (path, File.ReadAllText(path));
    }

    /// <summary>
    /// The report may stay on the shared folder, so it shows no folder (no drive and no UNC path) and not the name of the
    /// user. The name counts as a whole word, as the report hides it: a user named "file" may still read "files".
    /// </summary>
    private static void AssertNoPathOrName(string report)
    {
        Assert.DoesNotContain(@":\", report, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\\", report, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new Regex($@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(Environment.UserName)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            report);
    }

    /// <summary>
    /// What would move a host that reads the environment to every interface (<see cref="KilerHostTests"/>), on a port of its
    /// own, so a listener there shows it.
    /// </summary>
    private static Dictionary<string, string> AllInterfaces(int port)
    {
        var everywhere = $"http://0.0.0.0:{port}";
        return new()
        {
            ["ASPNETCORE_URLS"] = everywhere,
            ["DOTNET_URLS"] = everywhere,
            ["URLS"] = everywhere,
            ["ASPNETCORE_HTTP_PORTS"] = Text(port),
            ["ASPNETCORE_PREFERHOSTINGURLS"] = "true",
            ["DOTNET_PREFERHOSTINGURLS"] = "true",
        };
    }

    /// <summary>The address from the <c>Open:</c> line: the root of 127.0.0.1 with the token in <c>t</c>. A failure does not show the token.</summary>
    private static Uri ReadEntryAddress(string line)
    {
        Assert.True(
            Uri.TryCreate(line[OpenPrefix.Length..], UriKind.Absolute, out var address)
                && address.Scheme == Uri.UriSchemeHttp
                && address.Host == "127.0.0.1"
                && address.AbsolutePath == "/"
                && EntryQuery().IsMatch(address.Query),
            $"Not the address of Kiler with a token: {KilerExe.MaskToken(line)}");
        return address;
    }

    private static string TokenOf(Uri entryAddress) => entryAddress.Query["?t=".Length..];

    /// <summary>A client of the server that keeps no cookies, follows no redirects and uses no proxy, with <paramref name="cookie"/> on every request.</summary>
    private static HttpClient CreateClient(Uri entryAddress, string? cookie = null)
    {
        var client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false, UseProxy = false })
        {
            BaseAddress = new Uri(entryAddress.GetLeftPart(UriPartial.Authority)),
        };
        if (cookie is not null)
        {
            client.DefaultRequestHeaders.Add("Cookie", cookie);
        }

        return client;
    }

    /// <summary>A UI file with its type and the headers of every UI file (<see cref="UiFiles"/>); returns its text.</summary>
    private static async Task<string> AssertUiFileAsync(HttpClient client, string path, string type)
    {
        using var response = await client.GetAsync(path, Cancel);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {response.StatusCode}");
        Assert.Equal(type, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(UiFiles.ContentSecurityPolicy, Assert.Single(response.Headers.NonValidated["Content-Security-Policy"]));
        Assert.Equal("DENY", Assert.Single(response.Headers.NonValidated["X-Frame-Options"]));
        Assert.Equal("nosniff", Assert.Single(response.Headers.NonValidated["X-Content-Type-Options"]));
        return await response.Content.ReadAsStringAsync(Cancel);
    }

    /// <summary>The process ended <paramref name="wait"/> after <paramref name="from"/> or later, taken before the action that starts the wait.</summary>
    private static void AssertStoppedAfter(long from, TimeSpan wait)
    {
        var elapsed = Stopwatch.GetElapsedTime(from);
        Assert.True(elapsed >= wait - Early, $"Kiler stopped after {elapsed}, before its wait of {wait}.");
    }

    /// <summary>One listener on the port, IPv4 and IPv6 counted, and it is 127.0.0.1: Kiler does not hold <c>[::1]</c> either (M1.5).</summary>
    private static void AssertListensOnlyOn127001(int port) =>
        Assert.Equal(IPAddress.Loopback, Assert.Single(Listeners(), endpoint => endpoint.Port == port).Address);

    private static IPEndPoint[] Listeners() => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();

    /// <summary>
    /// <c>DependentLoadFlags</c> from the load config of the exe: the folders that Windows searches for the DLLs that the exe
    /// imports. <c>dumpbin /loadconfig</c> shows it as "Dependent Load Flag".
    /// </summary>
    private static ushort ReadDependentLoadFlags(PEReader pe)
    {
        var directory = pe.PEHeaders.PEHeader!.LoadConfigTableDirectory;
        Assert.True(directory.Size > 0, "The exe has no load config.");
        var reader = pe.GetSectionData(directory.RelativeVirtualAddress).GetReader();
        // The first field is the size of the structure that the linker wrote.
        Assert.True(reader.ReadUInt32() >= DependentLoadFlagsOffset + sizeof(ushort), "The load config of the exe has no DependentLoadFlags.");
        reader.Offset = DependentLoadFlagsOffset;
        return reader.ReadUInt16();
    }

    [GeneratedRegex(@"^\?t=[A-Za-z0-9_-]{43}$")]
    private static partial Regex EntryQuery();

    [GeneratedRegex("<script [^>]*src=\"(/assets/index-[A-Za-z0-9_-]+\\.js)\"")]
    private static partial Regex PageScript();

    [GeneratedRegex(@"events-worker-[A-Za-z0-9_-]+\.js")]
    private static partial Regex WorkerScript();
}

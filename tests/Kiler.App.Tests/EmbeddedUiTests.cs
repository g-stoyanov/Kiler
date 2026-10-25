using System.Net;
using System.Text.RegularExpressions;

namespace Kiler.App.Tests;

/// <summary>
/// The UI that a Release build embeds in Kiler.exe: the real Vite build of <c>src/ui</c>. A Debug build embeds none
/// (Kiler.App.csproj), so these tests run in the configuration they fit. CI and <c>test.ps1</c> build Release.
/// </summary>
public class EmbeddedUiTests
{
#if DEBUG
    public static bool IsDebugBuild => true;
#else
    public static bool IsDebugBuild => false;
#endif

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(Skip = "A Debug build embeds no UI.", SkipWhen = nameof(IsDebugBuild))]
    public async Task Root_ReturnsTheEmbeddedPage_AndEveryFileThePageNames()
    {
        await using var server = await KilerServer.StartAsync();
        using var client = server.CreateSignedInClient();

        using var page = await client.GetAsync("/", Cancel);
        var html = await page.Content.ReadAsStringAsync(Cancel);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<div id=\"root\"></div>", html, StringComparison.Ordinal);
        var paths = Regex.Matches(html, "(?:src|href)=\"(/[^\"]*)\"").Select(match => match.Groups[1].Value).ToList();
        Assert.Contains(paths, path => path.StartsWith("/assets/index-", StringComparison.Ordinal) && path.EndsWith(".js", StringComparison.Ordinal));
        Assert.Contains(paths, path => path.StartsWith("/assets/index-", StringComparison.Ordinal) && path.EndsWith(".css", StringComparison.Ordinal));
        foreach (var path in paths)
        {
            using var file = await client.GetAsync(path, Cancel);
            Assert.True(file.StatusCode == HttpStatusCode.OK, $"{path}: {file.StatusCode}");
            Assert.Equal(TypeOf(path), file.Content.Headers.ContentType?.MediaType);
        }
    }

    // The worker is not named in the page but in the page's script, so the test finds it among the files.
    [Fact(Skip = "A Debug build embeds no UI.", SkipWhen = nameof(IsDebugBuild))]
    public async Task EventsWorker_IsEmbeddedAndServedAsJavaScript()
    {
        var worker = Assert.Single(
            UiFiles.Embedded().GetDirectoryContents("assets"),
            file => file.Name.StartsWith("events-worker-", StringComparison.Ordinal) && file.Name.EndsWith(".js", StringComparison.Ordinal));
        await using var server = await KilerServer.StartAsync();
        using var client = server.CreateSignedInClient();

        using var response = await client.GetAsync($"/assets/{worker.Name}", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(UiFiles.ContentSecurityPolicy, Assert.Single(response.Headers.NonValidated["Content-Security-Policy"]));
    }

    [Fact(Skip = "A Release build embeds the UI.", SkipUnless = nameof(IsDebugBuild))]
    public void Embedded_InADebugBuild_HasNoFiles()
    {
        Assert.False(UiFiles.Embedded().GetFileInfo("index.html").Exists);
    }

    private static string TypeOf(string path) => Path.GetExtension(path) switch
    {
        ".js" => "text/javascript",
        ".css" => "text/css",
        ".svg" => "image/svg+xml",
        var other => throw new InvalidOperationException($"The page names a file of an unexpected type: {other}"),
    };
}

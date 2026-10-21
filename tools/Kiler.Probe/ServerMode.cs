using System.ComponentModel;
using System.Diagnostics;
using System.Net;

namespace Kiler.Probe;

/// <summary>A minimal server on 127.0.0.1 and a "Probe OK" page in the default browser.</summary>
internal static class ServerMode
{
    private static readonly TimeSpan WaitForBrowser = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StopAfterPage = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(TimeSpan startup)
    {
        var report = new Report("server");
        report.Line($"Process start to Main: {ProbeInfo.Ms(startup)}");

        // The empty builder reads no config files, so nothing watches the exe folder on the share.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();

        var listening = TimeSpan.Zero;
        var pageServed = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);

        app.Run(async context =>
        {
            if (context.Request.Path != "/")
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync(Page(listening), context.RequestAborted);
            pageServed.TrySetResult(ProbeInfo.SinceProcessStart());
        });

        await app.StartAsync();
        listening = ProbeInfo.SinceProcessStart();
        var url = app.Urls.Single();
        report.Line($"Listening on {url}, {ProbeInfo.Ms(listening)} after process start");

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            report.Line("Default browser: started");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            report.Check("Default browser", false, ex.Message);
        }

        // Ctrl+C stops the application, which ends the waits below.
        var stopping = app.Lifetime.ApplicationStopping;
        await Task.WhenAny(pageServed.Task, Task.Delay(WaitForBrowser, stopping));
        if (pageServed.Task.IsCompletedSuccessfully)
        {
            report.Check("The browser loaded the page", true, $"{ProbeInfo.Ms(pageServed.Task.Result)} after process start");
            report.Line($"Stopping in {StopAfterPage.TotalSeconds:F0} s.");
            await Task.Delay(StopAfterPage, stopping).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        else
        {
            report.Check("The browser loaded the page", false,
                stopping.IsCancellationRequested ? "stopped with Ctrl+C" : $"no request in {WaitForBrowser.TotalMinutes:F0} min");
        }

        await InstallMode.DeleteOldExeAsync(report);
        await app.StopAsync();
        report.Save();
        return report.Failed ? 1 : 0;
    }

    private static string Page(TimeSpan listening) => $$"""
        <!doctype html>
        <html lang="en">
        <meta charset="utf-8">
        <title>Kiler.Probe</title>
        <style>body { font-family: system-ui, sans-serif; margin: 2rem; max-width: 40rem; }</style>
        <h1>Probe OK</h1>
        <p>The exe runs from: {{WebUtility.HtmlEncode(ProbeInfo.Location(AppContext.BaseDirectory))}}.</p>
        <p>From process start to listening: {{ProbeInfo.Ms(listening)}}.</p>
        <p>The probe stops by itself in a few seconds. This tab can be closed.</p>
        </html>
        """;
}

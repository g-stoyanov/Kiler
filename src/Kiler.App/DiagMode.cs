using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Kiler.App;

/// <summary>
/// <c>Kiler.exe --diag [&lt;folder&gt;]</c> from the console: the header of the report, the folder (<see cref="DiagTarget"/>),
/// the run (<see cref="FileDiag"/>) with Ctrl+C, and the report file next to the exe. It starts no server and copies
/// nothing, also from the shared folder.
/// </summary>
internal static class DiagMode
{
    /// <summary>The exit code: 0 when every check passed, 1 otherwise.</summary>
    public static async Task<int> RunAsync(StartOptions options)
    {
        var startup = SinceProcessStart();
        var time = TimeProvider.System;
        var report = new DiagReport(Console.Out);
        var exeFolder = AppContext.BaseDirectory;
        report.HidePath(exeFolder, "<exe folder>");
        report.HidePath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify), "<profile>");
        report.HideName(Environment.UserName, "<user>");

        report.Line(string.Create(CultureInfo.InvariantCulture, $"Kiler {HealthApi.Version}, --diag, {time.GetLocalNow():yyyy-MM-dd HH:mm:ss}"));
        report.Line($"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}");
        report.Line($"Exe location: {FileDiag.Location(exeFolder)}");
        report.Line($"Process start to Main: {DiagReport.Ms(startup)}");
        CheckRemoteImages(report);

        var share = ShareFile.ForCurrentUser();
        var files = new CopyFiles();
        var start = new LocalCopy(CopyPlace.ForCurrentUser(), share, files, files, new ShellShortcuts(), new ProcessStarter(), UpdateKey.PublicKey, HealthApi.Version)
            .Decide(Environment.ProcessPath);
        if (DiagTarget.Choose(options, start, share) is not { } folder)
        {
            report.Check("Test folder", false, "Kiler has not been started from the shared folder yet, so give the folder: Kiler.exe --diag <folder>");
        }
        else
        {
            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler onCtrlC = (_, e) =>
            {
                // The first Ctrl+C stops the run, which then cleans up. A second one ends the process at once.
                e.Cancel = !stop.IsCancellationRequested;
                stop.Cancel();
            };
            Console.CancelKeyPress += onCtrlC;
            try
            {
                await new FileDiag(report, time).RunAsync(folder, stop.Token);
            }
            finally
            {
                Console.CancelKeyPress -= onCtrlC;
            }
        }

        report.Save(exeFolder, time.GetLocalNow());
        return report.Failed ? 1 : 0;
    }

    /// <summary>
    /// Whether the policy of the start holds (M3.8): from the shared folder, a DLL planted next to the exe does not load
    /// after it. The trial use checks it there (docs/real-environment-checks.md).
    /// </summary>
    private static void CheckRemoteImages(DiagReport report)
    {
        const string What = "DLLs from a network path are refused";
        try
        {
            using var self = Process.GetCurrentProcess();
            report.Check(What, RemoteImages.AreForbidden(self.SafeHandle));
        }
        catch (Win32Exception ex)
        {
            report.Check(What, false, ex.Message);
        }
    }

    /// <summary>The time since Windows started the process, the load of the exe included: from the shared folder, most of it.</summary>
    private static TimeSpan SinceProcessStart()
    {
        using var self = Process.GetCurrentProcess();
        return TimeProvider.System.GetUtcNow() - self.StartTime.ToUniversalTime();
    }
}

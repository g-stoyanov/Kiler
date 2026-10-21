using System.Diagnostics;

namespace Kiler.Probe;

/// <summary>
/// The plan for Kiler.exe after M1.0b: started from the share, it copies itself to the user's profile and
/// runs from there; an update renames the running exe and moves the new one into its place.
/// These steps check that the antivirus lets an exe do that.
/// </summary>
internal static class InstallMode
{
    private const string ExeName = "Kiler.Probe.exe";
    private const string OldExeName = "Kiler.Probe.old.exe";

    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string InstallFolder = Path.Combine(LocalAppData, "Programs", "Kiler.Probe");

    /// <summary>True for the copy in the profile, which must not install itself again.</summary>
    public static bool RunsFromInstallFolder { get; } = string.Equals(
        Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), InstallFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>Step 1, from the share: copy this exe to the profile and start the copy.</summary>
    public static int Install(TimeSpan startup)
    {
        var report = new Report("install");
        report.Hide(InstallFolder, @"%LOCALAPPDATA%\Programs\Kiler.Probe");
        report.Line($"Process start to Main: {ProbeInfo.Ms(startup)}");
        var target = Path.Combine(InstallFolder, ExeName);
        var temp = target + ".tmp";
        try
        {
            if (LocalAppData.Length == 0)
            {
                throw new InvalidOperationException("The local application data folder is unknown.");
            }

            var source = Environment.ProcessPath ?? throw new InvalidOperationException("The path of this exe is unknown.");
            Directory.CreateDirectory(InstallFolder);

            // Through a temporary file, so an interrupted copy never leaves a broken exe under the real name.
            var watch = Stopwatch.StartNew();
            File.Copy(source, temp, overwrite: true);
            File.Move(temp, target, overwrite: true);
            report.Check($"Copy to {target}", true,
                $"{new FileInfo(target).Length / (1024 * 1024)} MB in {ProbeInfo.Ms(watch.Elapsed)}");

            Start(target, "--self-update");
            report.Check("Start the copy", true);
        }
        catch (Exception ex)
        {
            // The probe is there to find out what the antivirus blocks, so it reports instead of crashing.
            report.Check("Install", false, $"{ex.GetType().Name}: {ex.Message}");
            TryDelete(temp);
        }

        report.Save();
        return report.Failed ? 1 : 0;
    }

    /// <summary>
    /// Step 2, in the profile: replace the running exe the way an update will, then start the new one.
    /// The new exe is written next to it first, so a failure never leaves the real name empty or half-written.
    /// </summary>
    public static int SelfUpdate(TimeSpan startup)
    {
        var report = new Report("self-update");
        report.Line($"Process start to Main: {ProbeInfo.Ms(startup)}");
        if (!RunsFromInstallFolder)
        {
            report.Check("Self-update", false, "runs only in the copy in the profile");
            report.Save();
            return 2;
        }

        var self = Path.Combine(InstallFolder, ExeName);
        var fresh = self + ".new";
        var old = Path.Combine(InstallFolder, OldExeName);
        try
        {
            File.Delete(old);

            // A copy of itself stands in for the new version.
            File.Copy(self, fresh, overwrite: true);
            report.Check("Write the new exe next to the running one", true);

            // A running exe cannot be overwritten, but it can be renamed.
            File.Move(self, old);
            report.Check("Rename the running exe", true);

            File.Move(fresh, self);
            report.Check("Move the new exe into its place", true);

            Start(self, "--server");
            report.Check("Start the new exe", true);
        }
        catch (Exception ex)
        {
            report.Check("Self-update", false, $"{ex.GetType().Name}: {ex.Message}");
            TryDelete(fresh);
            if (!File.Exists(self) && File.Exists(old))
            {
                try
                {
                    File.Move(old, self);
                    report.Line("The old exe was moved back.");
                }
                catch (Exception moveBack) when (moveBack is IOException or UnauthorizedAccessException)
                {
                    report.Check("Move the old exe back", false, moveBack.Message);
                }
            }
        }

        report.Save();
        return report.Failed ? 1 : 0;
    }

    /// <summary>Step 3, in the new exe: the old one can be deleted once its process has exited and the antivirus has let go of it.</summary>
    public static async Task DeleteOldExeAsync(Report report)
    {
        var old = Path.Combine(InstallFolder, OldExeName);
        if (!RunsFromInstallFolder || !File.Exists(old))
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Delete(old);
                report.Check("Delete the old exe after the update", true, $"attempt {attempt}");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 5)
                {
                    report.Check("Delete the old exe after the update", false, ex.Message);
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
        }
    }

    private static void Start(string exe, string argument) =>
        Process.Start(new ProcessStartInfo(exe, argument)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe),
        })?.Dispose();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Litter at worst: it is never under the real name.
        }
    }
}

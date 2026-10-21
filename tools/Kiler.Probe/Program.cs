using System.Runtime.Versioning;
using Kiler.Probe;

[assembly: SupportedOSPlatform("windows")]

var startup = ProbeInfo.SinceProcessStart();

return args switch
{
    // From the share a double click installs, as Kiler.exe will (see InstallMode).
    [] when ProbeInfo.IsNetwork(AppContext.BaseDirectory) && !InstallMode.RunsFromInstallFolder => InstallMode.Install(startup),
    [] or ["--server"] => await ServerMode.RunAsync(startup),
    ["--self-update"] => InstallMode.SelfUpdate(startup),
    ["--diag", var folder] => await DiagMode.RunAsync(folder, startup),
    ["--help" or "-h" or "/?"] => Usage(0),
    _ => Usage(2),
};

static int Usage(int exitCode)
{
    Console.WriteLine("""
        Kiler.Probe: checks for design section 14.

          Kiler.Probe.exe                  from a share: copies itself to the profile and runs from there
                                           elsewhere: starts a server on 127.0.0.1 and opens the browser
          Kiler.Probe.exe --server         starts the server, also from a share
          Kiler.Probe.exe --diag <folder>  measures many small files in a temporary subfolder
        """);
    return exitCode;
}

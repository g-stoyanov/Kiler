using System.Diagnostics;

namespace Kiler.Probe;

internal static class ProbeInfo
{
    /// <summary>Time since the OS started the process, including loading the exe.</summary>
    public static TimeSpan SinceProcessStart()
    {
        using var self = Process.GetCurrentProcess();
        return DateTime.Now - self.StartTime;
    }

    public static bool IsNetwork(string path) => IsUnc(path) || DriveKind(path) == DriveType.Network;

    /// <summary>What kind of storage a path is on, without the path itself.</summary>
    public static string Location(string path)
    {
        if (IsUnc(path))
        {
            return "network share (UNC path)";
        }

        return DriveKind(path) switch
        {
            DriveType.Network => "network drive (mapped letter)",
            DriveType.Fixed => "local disk",
            DriveType.Removable => "removable disk",
            DriveType other => other.ToString(),
            null => "unknown",
        };
    }

    public static string Ms(TimeSpan time) => $"{time.TotalMilliseconds:F0} ms";

    private static bool IsUnc(string path) => path.StartsWith(@"\\", StringComparison.Ordinal);

    private static DriveType? DriveKind(string path)
    {
        var root = Path.GetPathRoot(path);
        return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).DriveType;
    }
}

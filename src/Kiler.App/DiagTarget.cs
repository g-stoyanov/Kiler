namespace Kiler.App;

/// <summary>The folder that <c>--diag</c> measures.</summary>
internal static class DiagTarget
{
    /// <summary>The data folder in the shared folder (design 5.1).</summary>
    public const string DataSubfolder = "data";

    /// <summary>
    /// The folder after <c>--diag</c>; else the data folder: the one from <c>--data</c>, else <c>data\</c> in the shared
    /// folder. Started from a shared folder, that is the folder of the exe, which a server start from there would remember
    /// (M1.5a); started from the local copy, it is the folder in <c>share.json</c>. Null when there is none, because Kiler has
    /// never been started from a shared folder.
    /// </summary>
    public static string? Choose(StartOptions options, LocalCopyDecision start, ShareFile share)
    {
        if ((options.DiagFolder ?? options.DataFolder) is { } folder)
        {
            return folder;
        }

        var shared = start switch
        {
            LocalCopyDecision.CopyAndStart(var exe) => Path.GetDirectoryName(exe),
            LocalCopyDecision.StartCopy(var exe) => Path.GetDirectoryName(exe),
            _ => share.TryRead(),
        };
        return shared is null ? null : Path.Combine(shared, DataSubfolder);
    }
}

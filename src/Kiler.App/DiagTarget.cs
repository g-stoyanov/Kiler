namespace Kiler.App;

/// <summary>The folder that <c>--diag</c> measures.</summary>
internal static class DiagTarget
{
    /// <summary>
    /// The folder after <c>--diag</c>; else the data folder, by the rule of a server start (<see cref="DataPlace.ChooseFolder"/>).
    /// Null when there is none, because Kiler has never been started from a shared folder.
    /// </summary>
    public static string? Choose(StartOptions options, LocalCopyDecision start, ShareFile share) =>
        options.DiagFolder ?? DataPlace.ChooseFolder(options.DataFolder, start, share);
}

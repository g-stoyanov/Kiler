namespace Kiler.App;

/// <summary>
/// Where the data of a start is (M2.10): the data folder, and the folder of its local copy in the profile,
/// <c>%LOCALAPPDATA%\Kiler\copies\&lt;key&gt;</c>, with the key of the instance names (<see cref="InstancePlace"/>). So the
/// copy is per data folder, not per instance: the Kiler that the user starts and one with <c>--data</c> on the same folder,
/// and the Kilers of two sessions, share it, and <see cref="KilerData"/> locks it. Another spelling of the same folder, such
/// as a mapped drive and its UNC path, gets a copy of its own.
/// </summary>
/// <param name="DataFolder">The full path of the data folder.</param>
/// <param name="LocalFolder"><c>%LOCALAPPDATA%\Kiler</c>, or a folder of a test.</param>
internal sealed record DataPlace(string DataFolder, string LocalFolder)
{
    /// <summary>The data folder in the shared folder (design 5.1).</summary>
    public const string DataSubfolder = "data";

    /// <summary>The folder of the local copies in <see cref="LocalFolder"/>.</summary>
    public const string CopiesSubfolder = "copies";

    /// <summary>The folder of all the local copies.</summary>
    public string CopiesFolder => Path.Combine(LocalFolder, CopiesSubfolder);

    /// <summary>The folder of the local copy of <see cref="DataFolder"/>.</summary>
    public string CopyFolder => Path.Combine(CopiesFolder, InstancePlace.Key(DataFolder));

    /// <summary>The place of a data folder, its path made full.</summary>
    public static DataPlace For(string dataFolder, string localFolder) => new(Path.GetFullPath(dataFolder), localFolder);

    /// <summary>
    /// The data folder of a start: the one from <c>--data</c>, else <c>data\</c> in the shared folder. Started from a shared
    /// folder, that is the folder of the exe, which a server start from there would remember (M1.5a); started from the local
    /// copy, it is the folder in <c>share.json</c>, which is not checked again (decided at the start of M2). Null when there
    /// is none, because Kiler has never been started from a shared folder. Nothing is checked or made here: a data folder
    /// that does not exist is the wizard's (M4). <c>--diag</c> takes the same rule (<see cref="DiagTarget"/>).
    /// </summary>
    public static string? ChooseFolder(string? dataFolder, LocalCopyDecision start, ShareFile share)
    {
        if (dataFolder is not null)
        {
            return dataFolder;
        }

        var shared = start switch
        {
            LocalCopyDecision.CopyAndStart(var exe) => Path.GetDirectoryName(exe),
            LocalCopyDecision.StartCopy(var exe) => Path.GetDirectoryName(exe),
            _ => share.TryRead(),
        };
        return shared is null ? null : Path.Combine(shared, DataSubfolder);
    }

    /// <summary>The place of the data folder of a start (<see cref="ChooseFolder"/>); null when there is none.</summary>
    public static DataPlace? Choose(string? dataFolder, LocalCopyDecision start, ShareFile share, string localFolder) =>
        ChooseFolder(dataFolder, start, share) is { } folder ? For(folder, localFolder) : null;
}

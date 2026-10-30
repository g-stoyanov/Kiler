using System.Security.Cryptography;
using System.Text;
using Kiler.Storage;

namespace Kiler.DemoData;

/// <summary>
/// Where the demo data goes: the data folder, and the folder of the local copy that Kiler makes of it in the profile at its
/// first start, <c>%LOCALAPPDATA%\Kiler\copies\&lt;key&gt;</c> (host.md, "Data"). The copy is per path, so one that a Kiler made
/// of an earlier data folder at the same path, such as a <c>dev-data</c> of the wizard, keeps the documents of that folder.
/// So the demo data is made only where neither folder is, or with <c>--replace</c>, which deletes both first (decided by the
/// user in M4.10).
/// </summary>
/// <param name="Folder">The full path of the data folder, without a trailing separator.</param>
/// <param name="CopyFolder">The full path of the folder of its local copy.</param>
internal sealed record DemoTarget(string Folder, string CopyFolder)
{
    /// <summary>The folder of the local copies in <c>%LOCALAPPDATA%\Kiler</c>: <c>DataPlace.CopiesSubfolder</c> in Kiler.App.</summary>
    private const string CopiesSubfolder = "copies";

    /// <summary>The file of the local copy that a running Kiler holds: <c>KilerData.LockFile</c> in Kiler.App.</summary>
    private const string LockFile = "lock";

    /// <summary>What a data folder holds at its top: the folders of its layout, history, trash, and the two documents of its root.</summary>
    private static readonly HashSet<string> DataFolderEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        DataFolder.Templates,
        DataFolder.Stashes,
        DataFolder.Users,
        DataFolder.Records,
        DataFolder.History,
        DataFolder.Trash,
        DataFolder.Audit,
        DataFolder.Private,
        DataFolder.Levels + ".json",
        DataFolder.Settings + ".json",
    };

    /// <summary>
    /// The place of <paramref name="dataFolder"/>, its path made full; <paramref name="localFolder"/> is
    /// <c>%LOCALAPPDATA%\Kiler</c>, or a folder of a test.
    /// </summary>
    public static DemoTarget For(string dataFolder, string localFolder)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataFolder));
        return new(folder, Path.Combine(localFolder, CopiesSubfolder, CopyKey(folder)));
    }

    /// <summary>
    /// The key of the local copy of a data folder: the same as <c>InstancePlace.Key</c> in Kiler.App, which is internal there; a
    /// test of each holds it to the same value.
    /// </summary>
    public static string CopyKey(string dataFolder)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataFolder)).ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(folder)).AsSpan(0, 8));
    }

    /// <summary>
    /// Makes the place ready for the demo data: null when it is, else why not, for the console. Without
    /// <paramref name="replace"/>, it refuses when the data folder or its local copy is there. With it, it deletes both, but
    /// only a data folder on a local disk that holds nothing but what a data folder holds, as a wrong path must not cost a
    /// team its data or a developer a folder; and nothing while a Kiler holds the copy.
    /// </summary>
    public string? Prepare(bool replace)
    {
        // Before any look at the disk, so a path on a network is never touched.
        if (replace && NotOnALocalDisk() is { } remote)
        {
            return remote;
        }

        var folderThere = Path.Exists(Folder);
        var copyThere = Path.Exists(CopyFolder);
        if (!folderThere && !copyThere)
        {
            return null;
        }

        if (!replace)
        {
            var there = folderThere && copyThere ? $"{Folder} and its local copy {CopyFolder} are"
                : folderThere ? $"{Folder} is"
                : $"The local copy of {Folder}, {CopyFolder}, is";
            return $"{there} there already. Run it again with --replace to delete what is there and make the demo data anew.";
        }

        if (folderThere && NotADataFolder() is { } other)
        {
            return other;
        }

        FileStream? held = null;
        if (copyThere)
        {
            try
            {
                held = new FileStream(Path.Combine(CopyFolder, LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return $"A Kiler runs on {Folder}, as it holds the local copy: stop it first.";
            }
        }

        // The lock is held while the data folder goes, so no Kiler opens the copy meanwhile; the copy goes after it.
        using (held)
        {
            if (folderThere)
            {
                Directory.Delete(Folder, recursive: true);
            }
        }

        if (copyThere)
        {
            Directory.Delete(CopyFolder, recursive: true);
        }

        return null;
    }

    private string? NotOnALocalDisk()
    {
        // A UNC path (\\server\share) has no drive letter; a mapped network drive is DriveType.Network.
        var root = Path.GetPathRoot(Folder);
        return root is { Length: 3 } && root[1] == ':' && new DriveInfo(root).DriveType == DriveType.Fixed
            ? null
            : $"--replace deletes a data folder only on a local disk, and {Folder} is not on one.";
    }

    private string? NotADataFolder()
    {
        var attributes = File.GetAttributes(Folder);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            return $"{Folder} is not a folder, or is a link, so --replace leaves it.";
        }

        return Directory.EnumerateFileSystemEntries(Folder).All(entry => DataFolderEntries.Contains(Path.GetFileName(entry)))
            ? null
            : $"{Folder} holds more than a data folder does, so --replace leaves it.";
    }
}

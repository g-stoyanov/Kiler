namespace Kiler.App;

/// <summary>The files that the local copy looks at and makes. Tests replace them.</summary>
internal interface ICopyFiles
{
    /// <summary>True for a UNC path and for a mapped network drive.</summary>
    bool IsOnNetwork(string path);

    bool Exists(string path);

    /// <summary>Copies <paramref name="source"/> to <paramref name="target"/>. False when the target exists: it is never replaced.</summary>
    bool CopyNew(string source, string target);
}

/// <summary>The files that the local copy reads and makes when it looks for a new version (M3.6). Tests replace them.</summary>
internal interface IUpdateFiles
{
    /// <summary>Opens a file of the shared folder to read. While it is open, no one can write it. It throws as a <see cref="FileStream"/> does.</summary>
    Stream OpenShared(string path);

    /// <summary>
    /// Makes the file of a new version under a name that is not there yet, to write and read. While it is open, no one else can
    /// write it, but it can be renamed (M3.7). It throws as a <see cref="FileStream"/> does, also when a file of that name is
    /// there: a file that the check did not make is never taken over.
    /// </summary>
    Stream CreateUpdate(string path);

    /// <summary>Deletes a file; false when it cannot. A file that is not there counts as deleted.</summary>
    bool TryDelete(string path);
}

internal sealed class CopyFiles : ICopyFiles, IUpdateFiles
{
    private const int BufferSize = 81920;

    public bool IsOnNetwork(string path) => IsNetworkPath(path);

    public bool Exists(string path) => File.Exists(path);

    /// <summary>
    /// Through a temporary file and a rename, so an interrupted copy never leaves a broken exe under the real name. The rename
    /// does not replace a file, so a copy that another start made at the same moment stays as it is.
    /// </summary>
    public bool CopyNew(string source, string target)
    {
        // The target is always a file in a folder: %LOCALAPPDATA%\Programs\Kiler or a test folder.
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(source, temporary);
            File.Move(temporary, target, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(target))
        {
            DeleteQuietly(temporary);
            return false;
        }
        catch
        {
            DeleteQuietly(temporary);
            throw;
        }
    }

    /// <summary>
    /// Others can still read it, and rename or delete it, so a release is not held up; whatever they do, the copy reads one
    /// file, and its hash is taken of the local file anyway.
    /// </summary>
    public Stream OpenShared(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize, FileOptions.SequentialScan);

    /// <summary>
    /// The hash of the file is read through this stream, and no one can write the file until it is disposed (M3.4, security
    /// review). Its name can still be deleted or renamed, as the swap of M3.7 renames it by path while it is held; the name
    /// is new for each check, so it is that check's file (security review of M3.6).
    /// </summary>
    public Stream CreateUpdate(string path) =>
        new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, BufferSize);

    public bool TryDelete(string path) => DeleteQuietly(path);

    public static bool IsNetworkPath(string path)
    {
        path = path.Replace('/', '\\');

        // A device path: \\?\UNC\server\share is a UNC path, and \\?\C:\ is a drive.
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            path = path[4..];
            if (path.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        // Only a drive letter has a drive type. Anything else, such as a volume GUID path, is taken as local.
        return path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':'
            && new DriveInfo(path[..1]).DriveType == DriveType.Network;
    }

    /// <summary>Litter at worst, when it fails: the file never has the real name.</summary>
    private static bool DeleteQuietly(string file)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

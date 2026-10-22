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

internal sealed class CopyFiles : ICopyFiles
{
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
            TryDelete(temporary);
            return false;
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

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

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Litter at worst: it never has the real name.
        }
    }
}

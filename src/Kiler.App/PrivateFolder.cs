using System.Security.AccessControl;
using System.Security.Principal;

namespace Kiler.App;

/// <summary>
/// <c>%LOCALAPPDATA%\Kiler</c> holds the token of the running Kiler and, from M2, the local copy of the data. It is made with a
/// protected ACL: only the current user and SYSTEM. Nothing comes from the profile, so neither the administrators nor the
/// AppContainer capabilities that the profile may grant, which are sandboxes of the same user.
/// </summary>
internal static class PrivateFolder
{
    /// <summary>Makes the folder when it is missing. A folder that exists is left as it is.</summary>
    public static void Create(string path)
    {
        if (Directory.Exists(path))
        {
            return;
        }

        // Only the folder itself gets the ACL. Its parent is %LOCALAPPDATA%, or a test folder.
        if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))) is { } parent)
        {
            Directory.CreateDirectory(parent);
        }

        // A folder that another start made at the same moment is left as it is, without an error.
        Security().CreateDirectory(path);
    }

    /// <summary>Writes a file into a private folder through a temporary file and a rename, so that a reader never gets half of it.</summary>
    public static void WriteFile(string path, byte[] content)
    {
        // The path is always a file in a folder: %LOCALAPPDATA%\Kiler or a test folder.
        Create(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>Full control for the user and SYSTEM, inherited by everything in the folder, and nothing inherited from above.</summary>
    public static DirectorySecurity Security()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("Windows gives no account for this process.");
        var security = new DirectorySecurity();
        // Windows 11 marked a DACL given at creation as protected even without this flag; the code does not rely on that.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var account in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                account,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
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

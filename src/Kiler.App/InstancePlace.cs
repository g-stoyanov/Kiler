using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Kiler.App;

/// <summary>
/// The names of one Kiler instance: its mutex and its file. The mutex is per user and session, and so is the file:
/// <c>%LOCALAPPDATA%</c> is shared by all sessions of the user, so the file name carries the session. A start with
/// <c>--data</c> is an instance of its own, so that tests with a data folder of their own do not meet the Kiler that
/// the user runs.
/// </summary>
internal sealed record InstancePlace(string MutexName, string FilePath)
{
    private const string Name = "Kiler.Instance";

    public static InstancePlace ForCurrentUser(string? dataFolder)
    {
        using var process = Process.GetCurrentProcess();
        return For(dataFolder, LocalFolder(), process.SessionId);
    }

    public static InstancePlace For(string? dataFolder, string localFolder, int sessionId)
    {
        if (dataFolder is null)
        {
            return new(Name, Path.Combine(localFolder, $"instance-{sessionId}.json"));
        }

        // Windows ignores the case of a path. Another spelling of the same folder, such as a mapped drive and
        // its UNC path, gives another instance.
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataFolder)).ToUpperInvariant();
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(folder)).AsSpan(0, 8));
        return new($"{Name}.{key}", Path.Combine(localFolder, $"instance-{sessionId}-{key}.json"));
    }

    /// <summary>
    /// <c>%LOCALAPPDATA%\Kiler</c>, in the profile of the user. By default only the user, SYSTEM and the administrators can
    /// read it; the profile may add AppContainer capabilities, which are sandboxes of the same user.
    /// </summary>
    private static string LocalFolder()
    {
        // The known folder from Windows, not the LOCALAPPDATA variable.
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (folder.Length == 0)
        {
            throw new InvalidOperationException("Windows gives no local application data folder for this user.");
        }

        return Path.Combine(folder, "Kiler");
    }
}

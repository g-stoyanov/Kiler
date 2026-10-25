using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Kiler.App;

/// <summary>
/// The names of one Kiler instance: its mutex, its file and its cookie. The mutex is per user and session, and so is the
/// file: <c>%LOCALAPPDATA%</c> is shared by all sessions of the user, so the file name carries the session. A start with
/// <c>--data</c> is an instance of its own, so that tests with a data folder of their own do not meet the Kiler that
/// the user runs.
/// </summary>
internal sealed record InstancePlace(string MutexName, string FilePath, string CookieName)
{
    private const string Name = "Kiler.Instance";
    private const string Cookie = "kiler";

    public static InstancePlace ForCurrentUser(string? dataFolder)
    {
        using var process = Process.GetCurrentProcess();
        return For(dataFolder, UserFolders.Kiler(), process.SessionId);
    }

    public static InstancePlace For(string? dataFolder, string localFolder, int sessionId)
    {
        if (dataFolder is null)
        {
            return new(Name, Path.Combine(localFolder, $"instance-{sessionId}.json"), Cookie);
        }

        var key = Key(dataFolder);
        return new($"{Name}.{key}", Path.Combine(localFolder, $"instance-{sessionId}-{key}.json"), $"{Cookie}-{key}");
    }

    /// <summary>
    /// The name of the cookie that holds the token. Cookies are not separated by port, so two Kilers that ran at the same
    /// time under one name, such as the dev mode and the Kiler of the user, would each overwrite the cookie of the other.
    /// One name per instance, not per port, because the port changes with every start and the browser keeps the old
    /// cookies, also after a restart that restores the session.
    /// </summary>
    public static string CookieNameFor(string? dataFolder) => dataFolder is null ? Cookie : $"{Cookie}-{Key(dataFolder)}";

    private static string Key(string dataFolder)
    {
        // Windows ignores the case of a path. Another spelling of the same folder, such as a mapped drive and
        // its UNC path, gives another instance.
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataFolder)).ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(folder)).AsSpan(0, 8));
    }
}

namespace Kiler.App;

/// <summary>The folders of Kiler in the profile of the current user, from the known folders of Windows, not from environment variables.</summary>
internal static class UserFolders
{
    /// <summary>
    /// <c>%LOCALAPPDATA%\Kiler</c>: the instance file, the shared folder (<see cref="ShareFile"/>) and, from M2, the local copy of
    /// the data. It is made with a protected ACL (<see cref="PrivateFolder"/>).
    /// </summary>
    public static string Kiler() => Path.Combine(Known(Environment.SpecialFolder.LocalApplicationData), "Kiler");

    /// <summary><c>%LOCALAPPDATA%\Programs\Kiler</c>: the local copy of Kiler.exe, where Windows puts the programs of one user.</summary>
    public static string Programs() => Path.Combine(Known(Environment.SpecialFolder.LocalApplicationData), "Programs", "Kiler");

    /// <summary>The programs in the Start menu of the user: <c>%APPDATA%\Microsoft\Windows\Start Menu\Programs</c>.</summary>
    public static string StartMenu() => Known(Environment.SpecialFolder.Programs);

    private static string Known(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        return path.Length > 0 ? path : throw new InvalidOperationException($"Windows gives no {folder} folder for this user.");
    }
}

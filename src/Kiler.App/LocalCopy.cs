using System.ComponentModel;

namespace Kiler.App;

/// <summary>Where the local copy of Kiler lives: the exe in the profile and its shortcut in the Start menu.</summary>
internal sealed record CopyPlace(string ExePath, string ShortcutPath)
{
    public string Folder => Path.GetDirectoryName(ExePath)!;

    public static CopyPlace ForCurrentUser() =>
        new(Path.Combine(UserFolders.Programs(), "Kiler.exe"), Path.Combine(UserFolders.StartMenu(), "Kiler.lnk"));
}

/// <summary>What a start does about the local copy.</summary>
internal abstract record LocalCopyDecision
{
    private LocalCopyDecision()
    {
    }

    /// <summary>Started from a local folder, or this is the local copy: the server runs in this process.</summary>
    public sealed record RunHere : LocalCopyDecision;

    /// <summary>Started from a network folder, and the profile has no copy yet: copy this exe there and start the copy.</summary>
    public sealed record CopyAndStart(string Source) : LocalCopyDecision;

    /// <summary>Started from a network folder, and the profile has a copy: start it as it is.</summary>
    public sealed record StartCopy(string Source) : LocalCopyDecision;
}

/// <summary>
/// Each user runs Kiler from a copy in the profile (design section 4.1). Everyone can write in the shared folder, so anyone
/// could replace the exe there and capture the passwords of everyone who starts it. Started from a network folder,
/// Kiler.exe copies itself to the profile when there is no copy yet, remembers the shared folder, makes a Start menu
/// shortcut and starts the copy. A copy that exists is never replaced: only the copy checks the signature of a new version
/// (M3). Still, every start from the shared folder runs the exe there first, so Kiler tells the user to start it from the
/// Start menu.
/// </summary>
internal sealed class LocalCopy(CopyPlace place, ShareFile share, ICopyFiles files, IShortcuts shortcuts, IProcessStarter processes)
{
    public LocalCopyDecision Decide(string? exePath)
    {
        if (exePath is null || IsTheCopy(exePath) || !files.IsOnNetwork(exePath))
        {
            return new LocalCopyDecision.RunHere();
        }

        return files.Exists(place.ExePath) ? new LocalCopyDecision.StartCopy(exePath) : new LocalCopyDecision.CopyAndStart(exePath);
    }

    /// <summary>Starts the copy with the arguments of this start; the exit code of this process.</summary>
    public int Start(LocalCopyDecision decision, StartOptions options, TextWriter output, TextWriter errors)
    {
        var source = decision switch
        {
            LocalCopyDecision.CopyAndStart(var exe) => exe,
            LocalCopyDecision.StartCopy(var exe) => exe,
            _ => throw new ArgumentException("Kiler runs here, so there is no copy to start.", nameof(decision)),
        };

        if (decision is LocalCopyDecision.CopyAndStart)
        {
            // Over the network this takes a few seconds.
            output.WriteLine($"Kiler copies itself to {place.Folder}.");
            try
            {
                files.CopyNew(source, place.ExePath);
            }
            // Kiler does not run from the shared folder instead: that is what the copy is for.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.WriteLine($"Kiler cannot copy itself to {place.Folder}: {ex.Message}");
                return 1;
            }
        }

        // The exe is always a file in a folder.
        var folder = Path.GetDirectoryName(source)!;
        var before = share.TryRead();
        try
        {
            share.Write(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.WriteLine($"Kiler cannot remember the shared folder in {share.Path}: {ex.Message}");
            return 1;
        }

        // The data and the updates come from this folder from now on, so a change is not silent.
        if (before is not null && !string.Equals(before, folder, StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine($"The shared folder changes from {before} to {folder}.");
        }

        // A shortcut that exists stays as it is: the user may have changed it.
        var hasShortcut = files.Exists(place.ShortcutPath);
        if (!hasShortcut)
        {
            try
            {
                shortcuts.Create(place.ShortcutPath, place.ExePath);
                hasShortcut = true;
            }
            // The shortcut is a convenience, so Kiler starts without it, whatever stopped it. A COM error can be any of
            // several exception types.
            catch (Exception ex)
            {
                errors.WriteLine($"Kiler did not make its Start menu shortcut: {ex.Message}");
            }
        }

        try
        {
            processes.Start(place.ExePath, options.ToServerArguments());
        }
        catch (Win32Exception ex)
        {
            errors.WriteLine($"Kiler cannot start its copy {place.ExePath}: {ex.Message}");
            return 1;
        }

        output.WriteLine($"Kiler starts from {place.Folder}.");

        // Anyone can replace the exe in the shared folder, and a start from there runs it before anything else.
        output.WriteLine(hasShortcut
            ? "From now on, start Kiler from the Start menu, not from the shared folder."
            : $"From now on, start {place.ExePath}, not the Kiler.exe in the shared folder.");
        return 0;
    }

    /// <summary>The copy never copies itself, also when the profile is on a network.</summary>
    private bool IsTheCopy(string exePath) => string.Equals(
        Path.GetDirectoryName(Path.GetFullPath(exePath)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(place.Folder)),
        StringComparison.OrdinalIgnoreCase);
}

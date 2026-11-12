using System.ComponentModel;
using Kiler.Core;
using Kiler.Crypto;

namespace Kiler.App;

/// <summary>Where the local copy of Kiler lives: the exe in the profile and its shortcut in the Start menu.</summary>
internal sealed record CopyPlace(string ExePath, string ShortcutPath)
{
    /// <summary>The name of the exe, in the shared folder and in the local copy.</summary>
    public const string ExeName = "Kiler.exe";

    public string Folder => Path.GetDirectoryName(ExePath)!;

    public static CopyPlace ForCurrentUser() =>
        new(Path.Combine(UserFolders.Programs(), ExeName), Path.Combine(UserFolders.StartMenu(), "Kiler.lnk"));

    /// <summary>
    /// Whether <paramref name="exePath"/> is the exe of the local copy, which alone looks for a new version (M3.6): an exe of
    /// another name in its folder, a publish folder and the tests never do.
    /// </summary>
    public bool IsTheExe(string? exePath) =>
        exePath is not null && string.Equals(Path.GetFullPath(exePath), Path.GetFullPath(ExePath), StringComparison.OrdinalIgnoreCase);
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
/// Kiler.exe copies itself to the profile when there is no copy yet, checks the copy against the signed <c>version.json</c>
/// next to it (M3.8), remembers the shared folder, makes a Start menu shortcut and starts the copy. A copy that exists is
/// never replaced: only the copy checks the signature of a new version (M3.6). Still, every start from the shared folder
/// runs the exe there first, so Kiler tells the user to start it from the Start menu.
/// </summary>
/// <param name="place">The local copy.</param>
/// <param name="share"><c>share.json</c>, which remembers the shared folder.</param>
/// <param name="files">Whether the exe is on a network and which files are there.</param>
/// <param name="updates">The files of the shared folder and of the first copy.</param>
/// <param name="shortcuts">The Start menu shortcut.</param>
/// <param name="processes">The start of the copy.</param>
/// <param name="publicKey">The key built into the exe: <see cref="UpdateKey.PublicKey"/>, a key of a test in the tests.</param>
/// <param name="ownVersion">The version of the running exe: <see cref="HealthApi.Version"/>.</param>
internal sealed class LocalCopy(
    CopyPlace place,
    ShareFile share,
    ICopyFiles files,
    IUpdateFiles updates,
    IShortcuts shortcuts,
    IProcessStarter processes,
    string publicKey,
    string ownVersion)
{
    /// <summary>Where a start from <paramref name="exePath"/> runs. It changes nothing, so <c>--diag</c> asks it too.</summary>
    public LocalCopyDecision Decide(string? exePath)
    {
        if (exePath is null || IsTheCopy(exePath) || !files.IsOnNetwork(exePath))
        {
            return new LocalCopyDecision.RunHere();
        }

        return files.Exists(place.ExePath) ? new LocalCopyDecision.StartCopy(exePath) : new LocalCopyDecision.CopyAndStart(exePath);
    }

    /// <summary>
    /// <see cref="Decide"/> for a start of the server. Started from a network folder without a copy, it first moves back a
    /// <c>Kiler.exe.old</c> that a swap cut off between its two renames left alone, and then finds that copy (decided by the
    /// user in M3.7a): the copy that was there takes only a version higher than its own, where a new copy would take whatever
    /// the shared folder holds.
    /// </summary>
    public LocalCopyDecision DecideStart(string? exePath, UpdateLeftovers leftovers)
    {
        var decision = Decide(exePath);
        if (decision is not LocalCopyDecision.CopyAndStart)
        {
            return decision;
        }

        leftovers.MoveBackOld();
        return Decide(exePath);
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

        // Kiler does not run from the shared folder instead of a copy that it cannot make or check: that is what the copy is for.
        if (decision is LocalCopyDecision.CopyAndStart)
        {
            // Over the network this takes a few seconds.
            output.WriteLine($"Kiler copies itself to {place.Folder} and checks the copy.");
            if (!CopyAndCheck(source, errors))
            {
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

    /// <summary>
    /// Copies <paramref name="source"/> to a temporary file next to the copy and checks it against the signed
    /// <c>version.json</c> next to <paramref name="source"/> before the rename to <c>Kiler.exe</c> (decided by the user on
    /// 2026-11-11, M3.8): the version must be that of this exe, and a signature of it and the SHA-256 of the copied bytes must
    /// verify with the built-in key. The exe that runs was read by path when it started, and anyone can change the shared
    /// folder in the seconds until the copy, so the copy is checked and not taken for the running exe. A shared folder without
    /// <c>version.json</c> is refused too (decided by the user in M3.8): whoever can delete it would otherwise skip the check.
    /// False with a line, and no temporary file left, on any refusal; then nothing else is written.
    /// </summary>
    private bool CopyAndCheck(string source, TextWriter errors)
    {
        // A copy that cannot tell its own version checks nothing, and never takes another for it.
        if (!ReleaseVersion.TryParse(ownVersion, out var own))
        {
            errors.WriteLine($"Kiler cannot check its copy, so it does not copy itself: its own version {ownVersion} is not major.minor.patch.");
            return false;
        }

        // The exe is always a file in a folder.
        var versionPath = Path.Combine(Path.GetDirectoryName(source)!, VersionFile.Name);
        var check = new ReleaseCheck(updates, publicKey, VersionFile.MaxExeSize, CancellationToken.None);
        var read = check.ReadVersionFile(versionPath);
        if (read is not VersionFileRead.Read(var release))
        {
            errors.WriteLine(read switch
            {
                VersionFileRead.Missing => $"The shared folder has no {VersionFile.Name}, so Kiler cannot check its copy and does not copy itself.",
                VersionFileRead.Unreadable(var message) => $"Kiler cannot read {versionPath}, so it cannot check its copy and does not copy itself: {message}",
                VersionFileRead.DoesNotFit(var invalid) => $"{versionPath} does not fit, so Kiler does not copy itself: {invalid}",
                _ => $"Kiler does not copy itself: a read of {VersionFile.Name} gave {read}.",
            });
            return false;
        }

        // A version.json of another version is no check of this exe. During a release the exe and version.json of the shared
        // folder change one after the other, so a moment later they may agree.
        if (release.Version != own)
        {
            errors.WriteLine($"{versionPath} is for Kiler {release.Version}, not for this Kiler {own}, so Kiler does not copy itself; if a new version is on its way there, start Kiler again in a few minutes.");
            return false;
        }

        // A name of its own, so two starts that copy at once never share one; one that a start leaves goes at a later start of
        // the copy, once no one holds it and it is old (UpdateLeftovers, M3.7a).
        var temporary = $"{place.ExePath}.{Guid.NewGuid():N}{CopyFiles.TmpEnding}";
        CopyCheck copied;
        try
        {
            copied = check.CopyAndCheck(source, temporary, release);
        }
        // A fault of the code, or a DLL that the check needs and that the policy of M3.8 refuses from the shared folder.
        // The check has deleted its temporary file.
        catch (Exception ex)
        {
            errors.WriteLine($"Kiler failed while it checked its copy, so it does not copy itself: {ex}");
            return false;
        }

        if (copied is not CopyCheck.Checked(var file))
        {
            errors.WriteLine(copied switch
            {
                CopyCheck.TooBig => $"{source} is larger than {check.MaxExeSize} bytes, so Kiler does not copy itself.",
                CopyCheck.NotSigned => $"{source} is not the Kiler {release.Version} that {VersionFile.Name} signs, so Kiler does not copy itself.",
                CopyCheck.Failed(var message) => $"Kiler cannot copy itself to {place.Folder}: {message}",
                _ => $"Kiler does not copy itself: a copy of {source} gave {copied}.",
            });
            return false;
        }

        // Renamed while it is held, so no one writes it between the check and the rename, and never over a copy that another
        // start made at the same moment. It is let go before the copy starts, as Windows does not run an exe that a handle can
        // write.
        try
        {
            updates.Rename(temporary, place.ExePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            check.Delete(temporary, file);

            // Another start has made the copy first: that copy starts.
            if (UpdateSwap.NameTaken(ex))
            {
                return true;
            }

            errors.WriteLine($"Kiler cannot copy itself to {place.Folder}: {ex.Message}");
            return false;
        }
        // Also after a fault of the code; disposing twice does nothing.
        finally
        {
            file.Dispose();
        }

        return true;
    }

    /// <summary>The copy never copies itself, also when the profile is on a network.</summary>
    private bool IsTheCopy(string exePath) => string.Equals(
        Path.GetDirectoryName(Path.GetFullPath(exePath)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(place.Folder)),
        StringComparison.OrdinalIgnoreCase);
}

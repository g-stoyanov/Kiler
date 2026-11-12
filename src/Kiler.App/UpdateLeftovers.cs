namespace Kiler.App;

/// <summary>
/// What the updates leave in the folder of the local copy (M3.7a), deleted there only, and only under the names that Kiler
/// gives:
/// <list type="bullet">
/// <item><c>Kiler.exe.old</c>, the exe that a swap put aside (M3.7), which Windows refuses to delete while a Kiler runs it;</item>
/// <item>a <c>Kiler.exe.&lt;random&gt;.new</c> that no Kiler holds, as a check that was cut off leaves its own (M3.6);</item>
/// <item>a <c>Kiler.exe.&lt;random&gt;.tmp</c> of a first copy that was cut off (M1.5a), once it is old.</item>
/// </list>
/// The local copy deletes them at each start, in the background before it looks for a new version (<see cref="Clean"/>),
/// so its swap finds no <c>.old</c> of an earlier one. A <c>.old</c> without a <c>Kiler.exe</c> is a copy whose swap ended
/// between its two renames, which the Start menu no longer finds: a start from the shared folder moves it back before it
/// looks for the copy (<see cref="MoveBackOld"/>), so it starts that copy, which takes only a version higher than its own,
/// rather than a new copy of whatever the shared folder holds. Both decided by the user in M3.7a. Nothing here ever stops a
/// start: a failure is a line.
/// </summary>
/// <param name="place">The local copy, whose folder alone is changed.</param>
/// <param name="files">The listing, the deletes and the rename.</param>
/// <param name="time">The clock that tells an old <c>.tmp</c>.</param>
/// <param name="output">Where what was done, and a file that stays, is said.</param>
/// <param name="errors">Where what failed is said.</param>
/// <param name="stopping">Kiler stops: nothing more is deleted or said.</param>
/// <param name="tryDelay">The wait between the tries of a file; none in the tests.</param>
internal sealed class UpdateLeftovers(CopyPlace place, ILeftoverFiles files, TimeProvider time, TextWriter output, TextWriter errors, CancellationToken stopping, TimeSpan tryDelay)
{
    /// <summary>
    /// How old a <c>.tmp</c> must be before it goes (decided by the user in M3.7a). One that a handle holds stays at any age,
    /// so the age guards only the moment between the end of a first copy and its rename, and a copy whose handle is not seen.
    /// </summary>
    public static readonly TimeSpan TmpAge = TimeSpan.FromHours(1);

    /// <summary>The tries of each file, as an antivirus may hold one a moment (M1.0b, and the swap of M3.7).</summary>
    private const int Tries = 4;

    /// <summary>The random part of the names of a check and of a first copy: a <see cref="Guid"/> in its "N" form.</summary>
    private const int RandomLength = 32;

    private static readonly TimeSpan TryWait = TimeSpan.FromMilliseconds(500);

    public UpdateLeftovers(CopyPlace place, ILeftoverFiles files, TimeProvider time, TextWriter output, TextWriter errors, CancellationToken stopping)
        : this(place, files, time, output, errors, stopping, TryWait)
    {
    }

    private enum Leftover
    {
        None,
        Old,
        New,
        Tmp,
    }

    private string ExeName => Path.GetFileName(place.ExePath);

    private string OldPath => place.ExePath + UpdateSwap.OldEnding;

    /// <summary>
    /// For a start from the shared folder, before it looks for the copy: moves <c>Kiler.exe.old</c> back to
    /// <c>Kiler.exe</c> when there is no <c>Kiler.exe</c>, without overwriting, in a few tries. It never throws.
    /// </summary>
    public void MoveBackOld()
    {
        try
        {
            if (files.Exists(place.ExePath) || Find(UpdateSwap.OldEnding) is not { } old)
            {
                return;
            }

            if (old.IsLink)
            {
                SayLink(old.Path);
                return;
            }

            for (var tries = 1; ; tries++)
            {
                try
                {
                    files.Rename(old.Path, place.ExePath);
                    Say(output, $"Kiler has moved its copy back from {OldPath} to {place.ExePath}, as an update had stopped halfway.");
                    return;
                }
                // Another start of the user has moved it back, or put an exe there: the copy is there. A folder that has gone
                // has nothing to move back (code review of M3.7a).
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException || UpdateSwap.NameTaken(ex))
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (tries == Tries || Wait())
                    {
                        Say(errors, $"Kiler cannot move its copy back from {OldPath} to {place.ExePath}: {ex.Message}");
                        return;
                    }
                }
            }
        }
        // No copy yet: the first start from the shared folder.
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception ex)
        {
            Say(errors, $"Kiler cannot look for what its updates left in {place.Folder}: {UpdateSwap.Reason(ex)}");
        }
    }

    /// <summary>
    /// For the local copy, before it looks for a new version: deletes the <c>.old</c> when there is a <c>Kiler.exe</c>, each
    /// <c>.new</c> that no Kiler holds, and each <c>.tmp</c> that is old and that no one holds, in a few tries each. It skips
    /// a reparse point and every other file. It never throws.
    /// </summary>
    public void Clean()
    {
        try
        {
            foreach (var file in files.List(place.Folder).OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (stopping.IsCancellationRequested)
                {
                    return;
                }

                CleanFile(file);
            }
        }
        // The copy runs from this folder, but it can still go while it runs.
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception ex)
        {
            Say(errors, $"Kiler cannot clear what its updates left in {place.Folder}: {UpdateSwap.Reason(ex)}");
        }
    }

    private void CleanFile(FolderFile file)
    {
        var leftover = KindOf(Path.GetFileName(file.Path));
        if (leftover is Leftover.None)
        {
            return;
        }

        // A delete through a handle would delete the target of a link, and the target is not Kiler's (M3.6, security review).
        if (file.IsLink)
        {
            SayLink(file.Path);
            return;
        }

        switch (leftover)
        {
            // Without a Kiler.exe it is the only exe of the copy: another Kiler of the user is between the two renames of its
            // swap, and a start from the shared folder moves a lone one back.
            case Leftover.Old when files.Exists(place.ExePath):
                Delete(file.Path, files.Delete);
                break;
            // The handle of a check holds its .new, which it renames or deletes itself (M3.6).
            case Leftover.New:
                Delete(file.Path, files.DeleteUnheld);
                break;
            // A parallel first copy may be writing it, or about to rename it (M1.5a).
            case Leftover.Tmp when !IsYoung(file):
                Delete(file.Path, files.DeleteUnheld);
                break;
        }
    }

    /// <summary>Deletes a file in a few tries; one that stays says so, as a later start tries again.</summary>
    private void Delete(string path, Action<string> delete)
    {
        for (var tries = 1; ; tries++)
        {
            try
            {
                delete(path);
                return;
            }
            // Gone already: another start of the user has deleted it, or the folder has gone (code review of M3.7a).
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (tries == Tries || Wait())
                {
                    Say(output, $"Kiler leaves {path} for a later start, as it cannot delete it now: {ex.Message}");
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Whether a first copy may still be writing a <c>.tmp</c>. <see cref="File.Copy(string, string)"/> keeps the write time
    /// of its source, so the later of the two times counts, and a time ahead of the clock counts as young.
    /// </summary>
    private bool IsYoung(FolderFile file) => time.GetUtcNow() - (file.Created > file.Written ? file.Created : file.Written) < TmpAge;

    private Leftover KindOf(string name)
    {
        if (string.Equals(name, ExeName + UpdateSwap.OldEnding, StringComparison.OrdinalIgnoreCase))
        {
            return Leftover.Old;
        }

        if (IsRandomName(name, UpdateCheck.NewEnding))
        {
            return Leftover.New;
        }

        return IsRandomName(name, CopyFiles.TmpEnding) ? Leftover.Tmp : Leftover.None;
    }

    /// <summary><c>Kiler.exe.&lt;32 hex digits&gt;&lt;ending&gt;</c>: the name that a check or a first copy gives, and no other.</summary>
    private bool IsRandomName(string name, string ending)
    {
        var start = ExeName.Length + 1;
        return name.Length == start + RandomLength + ending.Length
            && name.StartsWith(ExeName + ".", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(ending, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(name.AsSpan(start, RandomLength), "N", out _);
    }

    /// <summary>The file <c>Kiler.exe&lt;ending&gt;</c> of the folder of the copy, or null.</summary>
    private FolderFile? Find(string ending) => files.List(place.Folder)
        .FirstOrDefault(file => string.Equals(Path.GetFileName(file.Path), ExeName + ending, StringComparison.OrdinalIgnoreCase));

    /// <summary>Waits before the next try; true when Kiler stops meanwhile.</summary>
    private bool Wait() => stopping.WaitHandle.WaitOne(tryDelay);

    private void SayLink(string path) => Say(errors, $"Kiler leaves {path} as it is: it is a link, not a file that Kiler made.");

    /// <summary>Writes a line, unless Kiler stops, so no line comes after its last one.</summary>
    private void Say(TextWriter writer, string line)
    {
        if (stopping.IsCancellationRequested)
        {
            return;
        }

        try
        {
            writer.WriteLine(line);
        }
        catch (IOException)
        {
            // A console that has gone: the line is lost.
        }
    }
}

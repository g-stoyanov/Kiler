namespace Kiler.App;

/// <summary>
/// Puts a checked new version in the place of the local copy (design 4.1, M3.7), right after the check and without a
/// restart (decided by the user in M3.6): the running <c>Kiler.exe</c> is renamed to <c>Kiler.exe.old</c>, then the
/// <c>.new</c> takes its name, each rename without overwriting. Windows lets a running exe be renamed (M1.0b), so the
/// running Kiler goes on with its version, and the new one runs from the next start. A failure of the second rename moves
/// the old exe back; a swap that does not end in place deletes its <c>.new</c>.
/// <para>
/// No file IDs (decided by the user in M3.7): the image of a running Kiler is always at <c>Kiler.exe</c> or at
/// <c>.old</c>, and Windows refuses to delete a running exe, so a <c>.old</c> that a Kiler runs stays there and refuses the
/// first rename. That rename succeeds only while <c>Kiler.exe</c> is the exe that runs, also when another Kiler of the user
/// has put a new version in place already. The <c>.new</c> is renamed by its own name, new for each check. Putting another
/// file under either name needs the user's own processes, which is outside the model.
/// </para>
/// </summary>
/// <param name="place">The local copy, whose folder alone is changed.</param>
/// <param name="files">The renames and the deletes.</param>
/// <param name="output">Where what was done is said.</param>
/// <param name="errors">Where what failed is said.</param>
/// <param name="stopping">Kiler stops: no swap starts after it.</param>
/// <param name="moveBackDelay">The wait between the tries to move the old exe back; none in the tests.</param>
internal sealed class UpdateSwap(CopyPlace place, IUpdateFiles files, TextWriter output, TextWriter errors, CancellationToken stopping, TimeSpan moveBackDelay)
{
    /// <summary>The ending of the exe that a swap has put aside: <c>Kiler.exe.old</c>.</summary>
    public const string OldEnding = ".old";

    /// <summary>ERROR_ALREADY_EXISTS and ERROR_FILE_EXISTS as HRESULTs: a rename found its new name taken.</summary>
    private const int AlreadyExists = unchecked((int)0x800700B7);

    private const int FileExists = unchecked((int)0x80070050);

    /// <summary>
    /// The tries to move the old exe back: the antivirus that held the <c>.new</c> may hold the <c>.old</c> it has just
    /// become a moment longer, and without it the copy has no <c>Kiler.exe</c> (security review of M3.7, as the deletes of
    /// M1.0b). Some seconds at most, under the lock that the stop waits for.
    /// </summary>
    private const int MoveBackTries = 4;

    private static readonly TimeSpan MoveBackWait = TimeSpan.FromMilliseconds(500);

    private readonly Lock _lock = new();
    private bool _closed;

    public UpdateSwap(CopyPlace place, IUpdateFiles files, TextWriter output, TextWriter errors, CancellationToken stopping)
        : this(place, files, output, errors, stopping, MoveBackWait)
    {
    }

    /// <summary><c>Kiler.exe.old</c> next to the local copy.</summary>
    public string OldPath => place.ExePath + OldEnding;

    /// <summary>Whether a rename of <see cref="IUpdateFiles.Rename"/> failed because its new name is taken.</summary>
    public static bool NameTaken(Exception ex) => ex.HResult is AlreadyExists or FileExists;

    /// <summary>
    /// Puts <paramref name="update"/> in place, unless Kiler stops; true when it is there. It takes the update: the
    /// <c>.new</c> is let go, and deleted when it is not in place. A failure of the files and a fault of the code in a rename
    /// are lines, not exceptions; <see cref="UpdateCheck.StartInBackground"/> catches the rest and lets the <c>.new</c> go
    /// (code review of M3.7). Its lines are written while Kiler stops too, as <see cref="Close"/> waits for them, so they
    /// come before the last line of <c>Program</c>.
    /// </summary>
    public bool Run(CheckedUpdate update)
    {
        lock (_lock)
        {
            // The stop waits for a swap under way (Close), but no swap starts once Kiler stops: the next start checks again.
            if (_closed || stopping.IsCancellationRequested)
            {
                Discard(update);
                return false;
            }

            return Swap(update);
        }
    }

    /// <summary>
    /// Kiler stops: waits for a swap under way, which is local and short, and no swap starts after it. A process that ended
    /// between the two renames would leave the copy without <c>Kiler.exe</c> (M3.6).
    /// </summary>
    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
        }
    }

    private bool Swap(CheckedUpdate update)
    {
        var version = update.Version;
        try
        {
            files.Rename(place.ExePath, OldPath);
        }
        catch (IOException ex) when (NameTaken(ex))
        {
            // Another Kiler of the user has put a new version in place already, or runs from the .old still: no fault.
            Discard(update);
            Say(output, $"Kiler {version} goes into place at a later start, as {OldPath} is still there: a Kiler may still run from it.");
            return false;
        }
        catch (Exception ex)
        {
            Discard(update);
            Say(errors, $"Kiler cannot put Kiler {version} in place now, so it goes on with its version: {Reason(ex)}");
            return false;
        }

        try
        {
            files.Rename(update.Path, place.ExePath);
        }
        catch (Exception ex)
        {
            var notBack = MoveBack();
            Discard(update);
            // The fix by hand comes first: a start from the shared folder runs the exe there, which anyone can replace.
            Say(errors, notBack is null
                ? $"Kiler cannot put Kiler {version} in place, so it moves its own exe back and goes on with its version: {Reason(ex)}"
                : $"Kiler cannot put Kiler {version} in place: {Reason(ex)} Nor can it move its own exe back from {OldPath} to {place.ExePath}: {Reason(notBack)} If there is no {CopyPlace.ExeName} there, rename it back by hand: until then Kiler does not start from the Start menu.");
            return false;
        }

        // Let go only now, so no one could write the new exe before it had its name.
        update.Dispose();
        Say(output, $"Kiler {version} is in place. It runs from the next start of Kiler.");
        return true;
    }

    /// <summary>
    /// Moves the old exe back to its name, without overwriting, in a few tries; the exception of the last try when it cannot.
    /// A name that is taken is not tried again: another Kiler of the user, or a new copy from the shared folder, has put an
    /// exe there.
    /// </summary>
    private Exception? MoveBack()
    {
        for (var tries = 1; ; tries++)
        {
            try
            {
                files.Rename(OldPath, place.ExePath);
                return null;
            }
            catch (Exception ex)
            {
                if (tries == MoveBackTries || NameTaken(ex))
                {
                    return ex;
                }

                Thread.Sleep(moveBackDelay);
            }
        }
    }

    /// <summary>
    /// Deletes the <c>.new</c> while it is still held, so its name is this check's file, then lets it go; an antivirus that
    /// reads it may refuse the delete until then, so it is tried once more (as the check does, M3.6).
    /// </summary>
    private void Discard(CheckedUpdate update)
    {
        var deleted = files.TryDelete(update.Path);
        update.Dispose();
        if (!deleted)
        {
            files.TryDelete(update.Path);
        }
    }

    /// <summary>The message of an error of the files, and the whole exception of a fault of the code.</summary>
    public static string Reason(Exception ex) => ex is IOException or UnauthorizedAccessException ? ex.Message : ex.ToString();

    private static void Say(TextWriter writer, string line)
    {
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

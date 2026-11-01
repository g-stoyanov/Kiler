using Kiler.Storage;

namespace Kiler.Core;

/// <summary>
/// The stashes of an admin (M6.22, PLAN M5 steps 9 and 12, design 6.6; decided by the user, docs/tasks/M6.md, "Rename, merge
/// and delete"): the live stashes with the number of the shared records whose path has each (<see cref="List"/>), and the
/// rename, the merge into another live stash and the delete of a stash that no shared record has. Each is over the version of
/// the stash that the admin read, and replaces the file of the stash through <see cref="Records"/>, which takes it into its
/// set (<see cref="Records.Replace(StashFile, int)"/>): a merge writes <c>redirect</c> and a delete <c>deleted</c>, and the
/// records are not written again, as their paths are read through the stashes (<see cref="RecordSet.PathOf"/>, M6.21). The
/// caller has checked <see cref="Need.Admin"/>.
/// </summary>
public static class StashActions
{
    /// <summary>The 400 of a rename whose name is blank, with <c>field</c> <c>name</c>, as for a new stash.</summary>
    public const string BlankName = "The name of the stash is blank.";

    /// <summary>The 400 of a rename whose name makes the stash larger than a document, with <c>field</c> <c>name</c>.</summary>
    public const string LongName = "The name of the stash is too long.";

    /// <summary>The 400 of a merge without the stash to merge into, with <c>field</c> <c>into</c>.</summary>
    public const string NoTarget = "Choose the stash to merge into.";

    /// <summary>The 400 of a merge of a stash into itself, with <c>field</c> <c>into</c>.</summary>
    public const string IntoItself = "A stash cannot be merged into itself.";

    /// <summary>The 400 of a merge into a stash that is not live, with <c>field</c> <c>into</c>.</summary>
    public const string TargetGone = "The stash to merge into is not there, or has been merged or deleted. Choose another.";

    /// <summary>
    /// Every live stash whose file fits, by name without regard to case, with its version and the number of the shared records
    /// of <paramref name="set"/> whose path, read through (<see cref="RecordSet.PathOf"/>), has it; a record in the trash does
    /// not count, nor one whose file does not fit.
    /// </summary>
    public static IReadOnlyList<StashCount> List(RecordSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var counts = Counts(set);
        var stashes = new List<StashCount>();
        foreach (var entry in set.Stashes.Values)
        {
            if (entry is { Model: { IsLive: true } stash, Version: { } version })
            {
                stashes.Add(new(stash.Id, stash.Name, version, counts.GetValueOrDefault(stash.Id)));
            }
        }

        return [.. stashes.OrderBy(stash => stash.Name, StringComparer.OrdinalIgnoreCase).ThenBy(stash => stash.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Renames the live stash <paramref name="id"/> over <paramref name="version"/>: the name trimmed and not blank, and unique
    /// without regard to case among the live stashes but itself, else <see cref="StashOutcome.Exists"/>, so a change of case of
    /// its own name is taken, and so is the name of a merged or deleted stash, as for a new stash.
    /// </summary>
    /// <exception cref="SessionLockedException">The session is locked.</exception>
    public static StashResult Rename(Session admin, Records records, string id, int version, string? name, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        var set = Read(admin, records, id);
        if (Live(set, id, version) is not { } stash)
        {
            return Refusal(set, id);
        }

        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return Refused("name", BlankName);
        }

        if (set.Stashes.Values.Any(entry => entry.Model is { IsLive: true } other && other.Id != id && string.Equals(other.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return new(StashOutcome.Exists);
        }

        return Write(records, new StashFile(id, trimmed, Changed(stash, admin, time)), version);
    }

    /// <summary>
    /// Merges the live stash <paramref name="id"/> over <paramref name="version"/> into the live stash <paramref name="into"/>:
    /// its file keeps its name and gets <c>redirect</c>, so each path that has it reads <paramref name="into"/> at its place
    /// (<see cref="RecordSet.PathOf"/>). A merge into itself, or into a stash that is missing, does not fit or is merged or
    /// deleted, is refused, so no merge makes a loop.
    /// </summary>
    /// <exception cref="SessionLockedException">The session is locked.</exception>
    public static StashResult Merge(Session admin, Records records, string id, int version, string? into, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        var set = Read(admin, records, id);
        if (Live(set, id, version) is not { } stash)
        {
            return Refusal(set, id);
        }

        if (string.IsNullOrEmpty(into))
        {
            return Refused("into", NoTarget);
        }

        if (into == id)
        {
            return Refused("into", IntoItself);
        }

        if (set.Stashes.GetValueOrDefault(into)?.Model is not { IsLive: true })
        {
            return Refused("into", TargetGone);
        }

        return Write(records, new StashFile(id, stash.Name, Changed(stash, admin, time), redirect: into), version);
    }

    /// <summary>
    /// Deletes the live stash <paramref name="id"/> over <paramref name="version"/> when no shared record has it in its path,
    /// read through, else <see cref="StashOutcome.Used"/> with their number: its file keeps its name and gets <c>deleted</c>,
    /// so a record of the trash that has it, restored, is cut before it (<see cref="RecordSet.PathOf"/>).
    /// </summary>
    /// <exception cref="SessionLockedException">The session is locked.</exception>
    public static StashResult Delete(Session admin, Records records, string id, int version, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        var set = Read(admin, records, id);
        if (Live(set, id, version) is not { } stash)
        {
            return Refusal(set, id);
        }

        if (Counts(set).GetValueOrDefault(id) is var used and > 0)
        {
            return new(StashOutcome.Used, Records: used);
        }

        return Write(records, new StashFile(id, stash.Name, Changed(stash, admin, time), deleted: true), version);
    }

    private static RecordSet Read(Session admin, Records records, string id)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(id);
        return records.Read(admin);
    }

    /// <summary>The live stash <paramref name="id"/> of <paramref name="set"/> at <paramref name="version"/>; <c>null</c> when it is not, as <see cref="Refusal"/> says.</summary>
    private static StashFile? Live(RecordSet set, string id, int version) =>
        set.Stashes.GetValueOrDefault(id) is { Model: { IsLive: true } stash, Version: { } at } && at == version ? stash : null;

    /// <summary>Why <see cref="Live"/> gave no stash: one at another version is a conflict; one that is missing, does not fit, or is merged or deleted is not there.</summary>
    private static StashResult Refusal(RecordSet set, string id) =>
        set.Stashes.GetValueOrDefault(id)?.Model is { IsLive: true } ? new(StashOutcome.Conflict) : new(StashOutcome.Missing);

    /// <summary>The authorship of <paramref name="stash"/> with <paramref name="admin"/> and now as its last change.</summary>
    private static Authorship Changed(StashFile stash, Session admin, TimeProvider time) =>
        new(stash.Authorship.CreatedBy, stash.Authorship.CreatedAt, admin.Account, time.GetUtcNow().UtcDateTime);

    /// <summary>The number of the shared records whose path, read through, has each stash, by id.</summary>
    private static Dictionary<string, int> Counts(RecordSet set)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var view in set.Records.Values)
        {
            if (view.File.Model is { } record)
            {
                // A path read through has each stash once.
                foreach (var id in set.PathOf(record.Stash))
                {
                    counts[id] = counts.GetValueOrDefault(id) + 1;
                }
            }
        }

        return counts;
    }

    /// <summary>
    /// Replaces the version <paramref name="version"/> of the stash with <paramref name="stash"/> through
    /// <paramref name="records"/>, and gives how it ended, as the writes of <see cref="RecordActions"/> do (core.md, "Calling
    /// the layer").
    /// </summary>
    private static StashResult Write(Records records, StashFile stash, int version)
    {
        WriteResult written;
        try
        {
            written = records.Replace(stash, version);
        }
        catch (ArgumentException)
        {
            // A stash is not nested, so only the size of its name is refused.
            return Refused("name", LongName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(StashOutcome.NotWritten);
        }

        return written.Outcome switch
        {
            WriteOutcome.Written => new(StashOutcome.Done),

            // A slot in the way has the version that the write changes (core.md, "Calling the layer").
            WriteOutcome.Conflict when written.OnDisk!.Version == version => new(StashOutcome.Busy),
            WriteOutcome.Conflict => new(StashOutcome.Conflict),
            WriteOutcome.Missing => new(StashOutcome.Missing),
            WriteOutcome.Busy => new(StashOutcome.Busy),
            WriteOutcome.Offline => new(StashOutcome.Offline),
            _ => new(StashOutcome.NotWritten),
        };
    }

    private static StashResult Refused(string field, string invalid) => new(StashOutcome.Invalid, invalid, field);
}

/// <summary>A live stash as the admin's list has it (<see cref="StashActions.List"/>).</summary>
/// <param name="Id">The id of the stash.</param>
/// <param name="Name">Its name.</param>
/// <param name="Version">The version of its file, which a rename, a merge and a delete are over.</param>
/// <param name="Records">The number of the shared records whose path, read through, has it.</param>
public sealed record StashCount(string Id, string Name, int Version, int Records);

/// <summary>How an action of <see cref="StashActions"/> ended.</summary>
public enum StashOutcome
{
    /// <summary>The stash is written.</summary>
    Done,

    /// <summary>A value of the request does not fit; <see cref="StashResult.Invalid"/> says why, and <see cref="StashResult.Field"/> names it.</summary>
    Invalid,

    /// <summary>A rename to the name of another live stash, without regard to case.</summary>
    Exists,

    /// <summary>The stash is not there, its file does not fit, or it is merged or deleted, also meanwhile.</summary>
    Missing,

    /// <summary>The stash is at another version than the one that the admin read.</summary>
    Conflict,

    /// <summary>A delete of a stash that shared records have in their paths; <see cref="StashResult.Records"/> is their number.</summary>
    Used,

    /// <summary>Another write of the file is under way, or a file of it is held: the write can be tried again in a moment.</summary>
    Busy,

    /// <summary>The shared folder cannot be reached (<see cref="WriteOutcome.Offline"/>): the write can be made once it is back.</summary>
    Offline,

    /// <summary>The file could not be written: the share did not answer or refused it, or another outcome of the layer.</summary>
    NotWritten,
}

/// <param name="Outcome">How the action ended.</param>
/// <param name="Invalid">Why a value does not fit, a sentence for the user, with <see cref="StashOutcome.Invalid"/>; else null.</param>
/// <param name="Field">The key of what does not fit, <c>name</c> or <c>into</c>, with <see cref="StashOutcome.Invalid"/>; else null.</param>
/// <param name="Records">The number of the shared records that have the stash, with <see cref="StashOutcome.Used"/>; else 0.</param>
public sealed record StashResult(StashOutcome Outcome, string? Invalid = null, string? Field = null, int Records = 0);

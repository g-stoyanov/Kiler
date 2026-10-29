using System.Diagnostics.CodeAnalysis;
using Kiler.Core;
using Kiler.Storage;

namespace Kiler.App;

/// <summary>The wizard of a new Kiler (M4.3): the data folder, and the writes through the local copy.</summary>
internal sealed partial class KilerData
{
    /// <summary>Kept by the wizard for its whole run, so two of one Kiler never meet: the second finds what the first left.</summary>
    private readonly Lock _wizard = new();

    /// <summary>
    /// The wizard (M4.3): makes a new Kiler in the data folder of the start, only when it is not there
    /// (<see cref="DataState.Missing"/>) or holds nothing but the empty folders of the layout, which
    /// <see cref="DataFolder.Create"/> checks, so a missing <c>levels.json</c> beside other files never becomes a new Kiler
    /// (decided at the start of M4). Once the data folder is ready, <paramref name="make"/> makes the documents, which go
    /// through the local copy (<see cref="NewKiler.Write"/>): the open one, whose team then reads the copy again, as no load
    /// gives what this Kiler wrote (core.md, "The team"); or, while the data folder was missing, the copy of its path, which is
    /// then read and watched as at the start, whatever the writes gave, as the data folder is there by now, so the state goes
    /// <see cref="DataState.Opening"/> meanwhile. A data folder that it cannot make, such as one that another Kiler has made
    /// meanwhile, opens as at the start too. Either copy must never have had a document (<see cref="DataCopy.IsEmpty"/>): one
    /// that has may keep the last good bytes of a Kiler whose data folder has gone, which is damage that an admin restores, and
    /// new keys there would replace its keys (security review). It says on the console how it went.
    /// </summary>
    /// <param name="make">Makes the documents (<see cref="NewKiler.Make"/>); it runs only once the data folder is ready.</param>
    /// <param name="made">The documents that were written, with the recovery key for the answer; null when no Kiler was made.</param>
    /// <param name="refusal">Why no Kiler was made, for the page.</param>
    public bool TryMakeNew(Func<NewKiler> make, [NotNullWhen(true)] out NewKiler? made, out WizardRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(make);
        lock (_wizard)
        {
            var place = _place;
            DataCopy? copy;
            Team? team;
            lock (_gate)
            {
                var now = RefusalNow();
                if (now is not null || place is null)
                {
                    made = null;
                    refusal = now ?? WizardRefusal.NoFolder;
                    return false;
                }

                copy = _copy;
                team = _team;

                // As at the start, the open lets the copy go meanwhile, not Dispose, and the state is opening until its first
                // load has ended.
                if (copy is null)
                {
                    _opening = true;
                    _state = DataState.Opening;
                }
            }

            NewKiler? written = null;
            WizardRefusal? notWritten = null;
            bool HasHadDocuments(DataCopy into)
            {
                if (into.IsEmpty)
                {
                    return false;
                }

                SayError("The wizard cannot make a new Kiler in the data folder: its local copy has had the documents of a Kiler.");
                return true;
            }

            void Create(DataCopy into)
            {
                if (HasHadDocuments(into))
                {
                    return;
                }

                var documents = make();
                notWritten = Write(documents, into);
                written = notWritten is null ? documents : null;
            }

            if (copy is not null && team is not null)
            {
                // Before the check of the data folder, which would make one that has gone again; the copy writes through the
                // one that it opened.
                if (!HasHadDocuments(copy) && MakeFolder(place) is not null)
                {
                    Create(copy);
                    Update(team.Reload);
                }
            }
            else
            {
                _startedAt = _time.GetTimestamp();
                Open(place, Create);
            }

            made = written;

            // Without the writes, the data folder could not be made, or its copy not opened, which is said.
            refusal = notWritten ?? (State is DataState.Opening or DataState.Open or DataState.Offline ? WizardRefusal.NotNew : WizardRefusal.Unusable);
            return made is not null;
        }
    }

    /// <summary>Why the wizard cannot run now; null in the states that it runs in. Called under the lock.</summary>
    private WizardRefusal? RefusalNow() =>
        _stopping ? WizardRefusal.Stopping
        : _opening ? WizardRefusal.Opening
        : _state switch
        {
            DataState.Missing or DataState.Open => null,
            DataState.NoFolder => WizardRefusal.NoFolder,
            DataState.Opening => WizardRefusal.Opening,
            DataState.Offline => WizardRefusal.Offline,
            _ => WizardRefusal.Unusable,
        };

    /// <summary>Writes the documents of the wizard through the copy and says how it went; null when the Kiler was made.</summary>
    private WizardRefusal? Write(NewKiler kiler, DataCopy copy)
    {
        var written = kiler.Write(copy);
        if (written.Outcome == NewKilerOutcome.Made)
        {
            Say($"The wizard has made a new Kiler in the data folder {Folder}.");
            return null;
        }

        SayError(written.Outcome == NewKilerOutcome.Partial
            ? $"The wizard has made only part of a Kiler: {written.Failure} Stop Kiler, delete the data folder and its local copy {_place?.CopyFolder}, and start Kiler again for the wizard."
            : $"The wizard has made no Kiler: {written.Failure}");
        return written.Outcome switch
        {
            NewKilerOutcome.Exists => WizardRefusal.Exists,
            NewKilerOutcome.Offline => WizardRefusal.Offline,
            NewKilerOutcome.Partial => WizardRefusal.Partial,
            _ => WizardRefusal.NotWritten,
        };
    }
}

/// <summary>Why the wizard made no Kiler (<see cref="KilerData.TryMakeNew"/>), which <see cref="WizardApi"/> says to the page.</summary>
internal enum WizardRefusal
{
    /// <summary>No data folder was chosen (<see cref="DataState.NoFolder"/>).</summary>
    NoFolder,

    /// <summary>The data folder is opening (<see cref="DataState.Opening"/>), or an open of it runs.</summary>
    Opening,

    /// <summary>The data folder cannot be reached (<see cref="DataState.Offline"/>), and nothing was written.</summary>
    Offline,

    /// <summary>The data folder cannot be used now, as in a state without data; the console says why.</summary>
    Unusable,

    /// <summary>
    /// The data folder holds more than the empty folders of the layout, or it cannot be written, or the local copy has had the
    /// documents of a Kiler; the console says which.
    /// </summary>
    NotNew,

    /// <summary>Another wizard wrote <c>levels.json</c> first, and nothing was written.</summary>
    Exists,

    /// <summary><c>levels.json</c> could not be written, and nothing was written; the console says why.</summary>
    NotWritten,

    /// <summary><c>levels.json</c> was written, but not the rest; the console says what.</summary>
    Partial,

    /// <summary>Kiler stops.</summary>
    Stopping,
}

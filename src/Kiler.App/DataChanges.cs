namespace Kiler.App;

/// <summary>
/// The changes of the stashes, the records, the templates and the trash, on which every page reads again what it shows (M6.11,
/// step 9 of M6): each load of the watch that changed something, a failed load and each change of offline, after the team and
/// the records have taken it (<see cref="KilerData"/>); and each write of this Kiler that an endpoint with
/// <see cref="EventsApi.ChangesData"/> answered with success, as no load gives it (M2.7a), so the other pages of this Kiler
/// see it too. The event stream sends <c>changed</c> at each (<see cref="EventsApi"/>).
/// </summary>
internal sealed class DataChanges
{
    /// <summary>
    /// Raised on the thread of the watch or of the request. A handler keeps its work short and does not throw, as the handlers
    /// of the watch must not (M2.8): the event stream only writes to a channel.
    /// </summary>
    public event EventHandler? Changed;

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

namespace Kiler.Storage;

/// <summary>
/// A create of the private folder through the local copy while it is offline (<see cref="DataCopy.Offline"/>, M2.8a): the last
/// load could not reach the share, so the create was refused before it touched it. The writes of documents give
/// <see cref="WriteOutcome.Offline"/> instead (decided in M2.8a, by the user). It can be tried again once the share is back.
/// </summary>
public sealed class OfflineException : IOException
{
    public OfflineException()
        : base("The shared folder cannot be reached, so the local copy is offline.")
    {
    }
}

namespace Kiler.Storage;

/// <summary>
/// Which documents to report as edited in place (design 7.8, M2.3). A write of Kiler sets the creation time of its new file
/// to its write time before the rename and again after it, because the rename can bring back the creation time of a file
/// that had the name in the last 15 s (NTFS file tunneling). Until the second time is set, the file looks edited in place,
/// and another machine can go on seeing that for about 10 s through its SMB cache. So a document is reported only when the
/// same size and times, with a write time later than the creation time, have been seen for <see cref="SettleTime"/> on the
/// clock of this machine. Not safe across threads: one watching loop uses it (M2.8).
/// </summary>
internal sealed class InPlaceEdits(TimeProvider time)
{
    /// <summary>Longer than the SMB cache of about 10 s and the watching of about every 10 s together.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(30);

    private readonly Dictionary<DocumentKey, Seen> _seen = [];

    /// <summary>Whether to report the document as edited in place, given its stamp from a listing now.</summary>
    public bool ShouldReport(DocumentKey key, FileStamp stamp)
    {
        if (!stamp.LooksEditedInPlace)
        {
            _seen.Remove(key);
            return false;
        }

        if (!_seen.TryGetValue(key, out var seen) || seen.Stamp != stamp)
        {
            _seen[key] = new Seen(stamp, time.GetTimestamp());
            return false;
        }

        return time.GetElapsedTime(seen.Since) >= SettleTime;
    }

    /// <summary>Forgets a document that is no longer listed.</summary>
    public void Forget(DocumentKey key) => _seen.Remove(key);

    private readonly record struct Seen(FileStamp Stamp, long Since);
}

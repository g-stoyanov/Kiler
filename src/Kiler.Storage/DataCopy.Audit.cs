namespace Kiler.Storage;

/// <summary>
/// The audit of the secret values through the local copy (M6.17, design 12): an append goes straight to the share, and a read
/// reads the share, as the copy does not take <c>audit\</c> (decided at the start of M6.16 to M6.24). Each is refused while the
/// copy is offline, before it touches the share, as a write is (M2.8a): it looks before the lock and again under it.
/// </summary>
public sealed partial class DataCopy
{
    /// <summary>
    /// Appends a line to the audit of an account for a day on the share (<see cref="DataFolder.AppendAudit"/>):
    /// <see cref="WriteOutcome.Written"/>, or <see cref="WriteOutcome.Offline"/> while the copy is offline, with nothing
    /// written. A share that cannot be reached, a link and any other failure throw, as for a write of a document. A bad
    /// account or line is an <see cref="ArgumentException"/>.
    /// </summary>
    public WriteOutcome AppendAudit(DateOnly day, string account, ReadOnlySpan<byte> line)
    {
        if (_offline)
        {
            return WriteOutcome.Offline;
        }

        lock (_gate)
        {
            if (_offline)
            {
                return WriteOutcome.Offline;
            }

            _share.AppendAudit(day, account, line);
            return WriteOutcome.Written;
        }
    }

    /// <summary>
    /// The lines of the audit of the days from <paramref name="first"/> to <paramref name="last"/>, both included, read from the
    /// share (<see cref="DataFolder.ReadAudit"/>). While the copy is offline, an <see cref="OfflineException"/>, before the share
    /// is touched; a share that cannot be reached throws as for a load.
    /// </summary>
    public AuditRead ReadAudit(DateOnly first, DateOnly last)
    {
        if (_offline)
        {
            throw new OfflineException();
        }

        lock (_gate)
        {
            if (_offline)
            {
                throw new OfflineException();
            }

            return _share.ReadAudit(first, last, _limits.Listed, _limits.Reports, DataFolder.MaxAuditBytes, DataFolder.MaxAuditFiles);
        }
    }
}

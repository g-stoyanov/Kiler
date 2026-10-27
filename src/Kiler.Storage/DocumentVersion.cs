namespace Kiler.Storage;

/// <summary>
/// One version of a document in its list of versions (M2.5, design 5.6): a history slot, or the current version, with the
/// size and the times of its file. A slot is a new file that the change after its version made: the write of the next
/// version, or the delete. So the times of the slot of version N are those of that change, and its write time says when the
/// version after N was written or N deleted; the times of the current version are those of its own write. The creation time
/// of a slot can be earlier than its write time, or come from a slot that a takeover deleted (file tunneling, M2.3), so
/// <see cref="FileStamp.LooksEditedInPlace"/> says nothing about a slot. The owner is a SID string (M2.6): that of the current
/// version comes through the handle its times come through, and that of a slot from an open after the listing that gave its
/// times, so a slot replaced in between gives the two of two files. The owner of the slot of version N is who wrote version
/// N + 1 or deleted N, and the owner of the current version who wrote it. So who wrote a version N from 2 on is the owner of the slot of N - 1, or of the current
/// version for the last one; who wrote version 1 is in no owner, only in the content. <c>null</c> when it cannot be read,
/// and for the slots older than the newest <see cref="DataFolder.SlotsWithOwners"/>, which are not opened for it.
/// </summary>
public readonly record struct DocumentVersion(int Version, FileStamp Stamp, bool IsCurrent, string? Owner);

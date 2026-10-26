namespace Kiler.Storage;

/// <summary>
/// One version of a document in its list of versions (M2.5, design 5.6): a history slot, or the current version, with the
/// size and the times of its file. A slot is a new file that the change after its version made: the write of the next
/// version, or the delete. So the times of the slot of version N are those of that change, and its write time says when the
/// version after N was written or N deleted; the times of the current version are those of its own write. The creation time
/// of a slot can be earlier than its write time, or come from a slot that a takeover deleted (file tunneling, M2.3), so
/// <see cref="FileStamp.LooksEditedInPlace"/> says nothing about a slot. The owners come with M2.6.
/// </summary>
public readonly record struct DocumentVersion(int Version, FileStamp Stamp, bool IsCurrent);

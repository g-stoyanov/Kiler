using System.Text.Json.Serialization;

namespace Kiler.Storage;

/// <summary>
/// The index of a local copy, <c>index.json</c> in its folder (M2.7): the data folder it is a copy of, an entry for each
/// document that the copy knows, in its folder or in the trash, and an entry for each history slot it keeps (M2.7a), of the
/// data folder or of a private folder (M2.7b): of the current user, or of another user, kept only for a restore (M2.7c). A format that Kiler does not know, such as the format 1
/// of M2.7 or 2 of M2.7a, is no index, and the load reads everything again. Format 3 adds the account of an entry and of a
/// slot (M2.7b), which a Kiler of format 2 would not read, and would take the private documents for those of the data folder.
/// </summary>
internal sealed record CopyIndex(int Format, string? Share, IReadOnlyList<CopyEntry?>? Files, IReadOnlyList<SlotEntry?>? History)
{
    public const int CurrentFormat = 3;
}

/// <summary>
/// A document in the index of a local copy (M2.7), in its folder or in the trash (<see cref="InTrash"/>, M2.7a), of the data
/// folder or of the private folder of <see cref="Account"/> (M2.7b), the current user's or another's (M2.7c). Of the file
/// on the share: the size and the times that the last listing gave, or none yet after a write of this Kiler
/// (<see cref="Unseen"/>), the owner, read through the handle it was read with (M2.6), and why it is damaged, when it is. Of
/// the document in the copy: when the copy first took it (<see cref="Taken"/>) and when the share stopped listing it
/// (<see cref="GoneAt"/>), on the clock of this machine; the bytes of the file as the share has them, in <c>data\</c>: their
/// version, their SHA-256 in lowercase hex and their length (<see cref="Copied"/>), or 0, <c>null</c> and 0 when the copy has
/// none, and whether this Kiler wrote them (<see cref="Own"/>). When the share's file is not a version that Kiler made
/// (<see cref="Outside"/>), the last good bytes are kept apart in <c>kept\</c> (<see cref="Kept"/>, decided at the start of
/// M2), and <see cref="Reported"/> says whether a load has reported it. <see cref="HistoryFrom"/>: the version from which the
/// next load takes the slots of the document's history, after a change (decided in M2.7a). The files are written before the
/// index, so a crash between them leaves new bytes beside an old entry; the hash tells them apart, also at the same version,
/// and the owner of the entry is never given with other bytes (security review of M2.7).
/// </summary>
internal sealed record CopyEntry
{
    /// <summary>The times of a stamp that no listing gives, for a file that this Kiler wrote and no load has read back yet.</summary>
    public static readonly DateTimeOffset Unseen = DateTimeOffset.MinValue;

    public string? Account { get; init; }

    public string? Folder { get; init; }

    public string? Id { get; init; }

    public bool InTrash { get; init; }

    public long Size { get; init; }

    public DateTimeOffset CreationTime { get; init; }

    public DateTimeOffset LastWriteTime { get; init; }

    public string? Owner { get; init; }

    public DocumentDamage? Damage { get; init; }

    public DateTimeOffset Taken { get; init; }

    public DateTimeOffset? GoneAt { get; init; }

    public int Version { get; init; }

    public string? Hash { get; init; }

    public long Copied { get; init; }

    public bool Own { get; init; }

    public OutsideReason? Outside { get; init; }

    public KeptCopy? Kept { get; init; }

    public bool Reported { get; init; }

    public int? HistoryFrom { get; init; }

    /// <summary>The stamp that a listing is compared with.</summary>
    [JsonIgnore]
    public FileStamp Stamp => new(Size, CreationTime, LastWriteTime);

    /// <summary>The share no longer lists the document; the copy keeps its last good bytes.</summary>
    [JsonIgnore]
    public bool Gone => GoneAt is not null;

    /// <summary>
    /// The last good bytes of the document: those in <c>data\</c> when the share's file is a version that Kiler made, else
    /// those kept apart, or none.
    /// </summary>
    [JsonIgnore]
    public KeptCopy? LastGood => Outside is null && Copied > 0 ? new KeptCopy(Version, Hash, Copied, Own) : Kept;
}

/// <summary>
/// The last good bytes of a document (M2.7a): their version, their SHA-256 in lowercase hex, their length, and whether this
/// Kiler wrote them, as then no load makes room with them (fourth security review of M2.7).
/// </summary>
internal sealed record KeptCopy(int Version, string? Hash, long Length, bool Own);

/// <summary>
/// A history slot that the local copy keeps (M2.7a): the document, of the data folder or of a private folder (M2.7b), the version, the SHA-256 and the length of its bytes, when
/// the copy took it, on the clock of this machine, as the oldest make room first, and whether it is this Kiler's own: the slot
/// of a replace or a delete of this Kiler, or bytes that this Kiler wrote, which make room only for each other (decided in
/// M2.7a, by the user). The others are the versions that the copy had from other Kilers, and the slots read from the history
/// of the share (<see cref="FromShare"/>), which give way to the bytes that the copy has of their version (third security
/// review of M2.7a). <see cref="InTrash"/>: a slot of the document in the trash, kept apart from those of the document in its
/// folder, as two documents of one id, which Kiler never makes, would otherwise take each other's slots (fifth security
/// review of M2.7a).
/// </summary>
internal sealed record SlotEntry(string? Account, string? Folder, string? Id, bool InTrash, int Version, string? Hash, long Length, DateTimeOffset Taken, bool Own, bool FromShare);

/// <summary>
/// The budget of a local copy (decided after the reviews of M2.7, by the user): anyone who can write the share can put any
/// number of files there, each up to <see cref="DocumentFormat.MaxSize"/>, and every Kiler would read them into memory and
/// copy them into the profile. <see cref="Bytes"/> and <see cref="Documents"/> bound the documents that the copy takes, in
/// their folders and in the trash, with the last good bytes it keeps apart; <see cref="HistoryBytes"/> and
/// <see cref="HistorySlots"/> the slots of the history, a budget of its own (decided in M2.7a, by the user);
/// <see cref="HistoryListings"/> and <see cref="HistoryReads"/> the history folders that one load lists and the bytes of slots
/// that it reads, as anyone can make every document change and every slot unreadable (security review of M2.7a);
/// <see cref="Listed"/> the entries of one listing of a folder; <see cref="Reports"/> the skipped entries that one load gives.
/// </summary>
internal sealed record CopyLimits(
    long Bytes,
    int Documents,
    int Listed,
    int Reports,
    long HistoryBytes,
    int HistorySlots,
    int HistoryListings,
    long HistoryReads,
    long OtherBytes,
    int OtherDocuments,
    long BytesPerOther,
    int DocumentsPerOther)
{
    /// <summary>
    /// 64 MiB and 20 000 documents, many times the 2–4 MB of 2000 records (design 5.3); as much again for the history, with
    /// 100 history folders and 16 MiB of slots a load; a folder of more than 40 000 entries, twice the documents; 1000 reports;
    /// and as much again for the private folders of other users in an admin's copy, 4 MiB and 1000 documents of them each
    /// (decided in M2.7c, by the user).
    /// </summary>
    public static CopyLimits Default { get; } = new(
        DataCopy.MaxBytes,
        DataCopy.MaxDocuments,
        2 * DataCopy.MaxDocuments,
        1000,
        DataCopy.MaxHistoryBytes,
        DataCopy.MaxHistorySlots,
        100,
        16L * 1024 * 1024,
        DataCopy.MaxOtherBytes,
        DataCopy.MaxOtherDocuments,
        DataCopy.MaxBytesPerOther,
        DataCopy.MaxDocumentsPerOther);
}

/// <summary>The JSON of the layer's own types, with source generation, as Native AOT has no reflection-based serialization.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CopyIndex))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;

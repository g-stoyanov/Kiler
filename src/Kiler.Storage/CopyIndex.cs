using System.Text.Json.Serialization;

namespace Kiler.Storage;

/// <summary>
/// The index of a local copy, <c>index.json</c> in its folder (M2.7): the data folder it is a copy of, and an entry for each
/// document that the copy knows. A format that Kiler does not know is no index, and the load reads everything again.
/// </summary>
internal sealed record CopyIndex(int Format, string? Share, IReadOnlyList<CopyEntry?>? Files)
{
    public const int CurrentFormat = 1;
}

/// <summary>
/// A document in the index of a local copy (M2.7). Of the file on the share: where it is, the size and the times that the
/// last listing gave, the owner, read through the handle it was read with (M2.6), and why it is damaged, when it is. Of the
/// document in the copy: when the copy first took it (<see cref="Taken"/>) and when the share stopped listing it
/// (<see cref="GoneAt"/>), on the clock of this machine; and its last good bytes: their version, their SHA-256 in lowercase
/// hex, and their length (<see cref="Copied"/>), or 0, <c>null</c> and 0 when the copy has none. A damaged or gone document
/// keeps its last good bytes, which count in the budget as long as they are on the disk (decided after the second security
/// review of M2.7, by the user); M2.7a keeps them apart. The files are written before the index, so a crash between them
/// leaves new bytes beside an old entry; the hash tells them apart, also at the same version, and the owner of the entry is
/// never given with other bytes (security review of M2.7).
/// </summary>
internal sealed record CopyEntry(
    string? Folder,
    string? Id,
    long Size,
    DateTimeOffset CreationTime,
    DateTimeOffset LastWriteTime,
    string? Owner,
    DocumentDamage? Damage,
    DateTimeOffset Taken,
    DateTimeOffset? GoneAt,
    int Version,
    string? Hash,
    long Copied)
{
    /// <summary>The stamp that a listing is compared with.</summary>
    [JsonIgnore]
    public FileStamp Stamp => new(Size, CreationTime, LastWriteTime);

    /// <summary>The share no longer lists the document; the copy keeps its last good bytes.</summary>
    [JsonIgnore]
    public bool Gone => GoneAt is not null;
}

/// <summary>
/// The budget of a local copy (decided after the reviews of M2.7, by the user): anyone who can write the share can put any
/// number of files there, each up to <see cref="DocumentFormat.MaxSize"/>, and every Kiler would read them into memory and
/// copy them into the profile. <see cref="Bytes"/> and <see cref="Documents"/> bound what the copy takes; <see cref="Listed"/>
/// the entries of one listing of a folder; <see cref="Reports"/> the skipped entries that one load gives.
/// </summary>
internal sealed record CopyLimits(long Bytes, int Documents, int Listed, int Reports)
{
    /// <summary>
    /// 64 MiB and 20 000 documents, many times the 2–4 MB of 2000 records (design 5.3); a folder of more than 40 000 entries,
    /// twice the documents; 1000 reports.
    /// </summary>
    public static CopyLimits Default { get; } = new(DataCopy.MaxBytes, DataCopy.MaxDocuments, 2 * DataCopy.MaxDocuments, 1000);
}

/// <summary>The JSON of the layer's own types, with source generation, as Native AOT has no reflection-based serialization.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CopyIndex))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;

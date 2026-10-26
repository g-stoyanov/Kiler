namespace Kiler.Storage;

/// <summary>
/// Where a document is in its root: an id in a document folder (<c>records\8f3a1c.json</c>), or a document at the root,
/// whose name is its id (<c>settings.json</c>). Both names pass <see cref="StorageName"/> when the key is made, so no path
/// is built from a name that was not checked. Whether the folder belongs to a root, <see cref="DataFolder"/> checks.
/// </summary>
public sealed record DocumentKey
{
    // No init accessors, so a "with" expression cannot put an unchecked name in.
    private DocumentKey(string? folder, string id)
    {
        Folder = folder;
        Id = id;
    }

    /// <summary>The document folder, or <c>null</c> for a document at the root.</summary>
    public string? Folder { get; }

    public string Id { get; }

    public static DocumentKey InFolder(string folder, string id) => new(StorageName.Check(folder), StorageName.Check(id));

    public static DocumentKey AtRoot(string name) => new(null, StorageName.Check(name));

    public override string ToString() => Folder is null ? Id : $@"{Folder}\{Id}";
}

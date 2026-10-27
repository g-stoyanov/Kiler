namespace Kiler.Storage;

/// <summary>
/// Where a document is in its root: an id in a document folder (<c>records\8f3a1c.json</c>), or a document at the root,
/// whose name is its id (<c>settings.json</c>), in the data folder or in the private folder of an account
/// (<see cref="Account"/>, M2.7b). Every name passes <see cref="StorageName"/> when the key is made, so no path is built from
/// a name that was not checked. Whether the folder belongs to a root, <see cref="DataFolder"/> checks.
/// </summary>
public sealed record DocumentKey
{
    // No init accessors, so a "with" expression cannot put an unchecked name in.
    private DocumentKey(string? account, string? folder, string id)
    {
        Account = account;
        Folder = folder;
        Id = id;
    }

    /// <summary>
    /// The account of the private folder <c>private\&lt;account&gt;\</c> that the document is in, or <c>null</c> for the data
    /// folder (decided in M2.7b, by the user), so a private record and a shared one of the same id are two keys.
    /// </summary>
    public string? Account { get; }

    /// <summary>The document folder, or <c>null</c> for a document at the root.</summary>
    public string? Folder { get; }

    public string Id { get; }

    public static DocumentKey InFolder(string folder, string id) => new(null, StorageName.Check(folder), StorageName.Check(id));

    public static DocumentKey AtRoot(string name) => new(null, null, StorageName.Check(name));

    /// <summary>The document of the same folder and id in the private folder of <paramref name="account"/> (M2.7b).</summary>
    public DocumentKey InPrivate(string account) => new(StorageName.Check(account), Folder, Id);

    public override string ToString()
    {
        var name = Folder is null ? Id : $@"{Folder}\{Id}";
        return Account is null ? name : $@"{DataFolder.Private}\{Account}\{name}";
    }
}

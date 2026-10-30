using System.Text.Json.Nodes;
using Kiler.Storage;

namespace Kiler.App.Tests;

/// <summary>
/// A data folder of made-up records (M2.10), written through the data layer as Kiler leaves them: a file written so has the
/// same creation and write time, and a bare write often does not, so it looks edited in place (M2.7a).
/// </summary>
internal sealed class MadeUpData
{
    private readonly DataCopy _writer;

    private MadeUpData(string folder, DataCopy writer)
    {
        Folder = folder;
        _writer = writer;
    }

    /// <summary>The full path of the data folder.</summary>
    public string Folder { get; }

    /// <param name="folder">The data folder: it is made, or taken when it is empty.</param>
    /// <param name="writerCopy">A folder for the local copy that writes the records; it is made when it is missing.</param>
    public static MadeUpData Create(string folder, string writerCopy)
    {
        var share = DataFolder.Create(folder, FileOwner.Instance);
        Directory.CreateDirectory(writerCopy);
        return new(Path.GetFullPath(folder), DataCopy.Open(share, Path.GetFullPath(writerCopy), TimeProvider.System));
    }

    public static DocumentKey Record(string id) => DocumentKey.InFolder("records", id);

    /// <summary>The file of a record in the data folder.</summary>
    public string RecordFile(string id) => Path.Combine(Folder, "records", $"{id}.json");

    /// <summary>The file of a record in a local copy of the data folder: its <c>data\</c> repeats the folders of the share (M2.7).</summary>
    public static string CopyFile(string copyFolder, string id) => Path.Combine(copyFolder, "data", "records", $"{id}.json");

    /// <summary>Makes a record at version 1.</summary>
    public void Create(string id) => Assert.Equal(WriteOutcome.Written, _writer.Create(Record(id), Content(id, 1)).Outcome);

    /// <summary>Makes a document of any other kind, such as a user file (M4.2), at version 1.</summary>
    public void Create(DocumentKey key, JsonObject content) => Assert.Equal(WriteOutcome.Written, _writer.Create(key, content).Outcome);

    /// <summary>Writes a record over its <paramref name="version"/>.</summary>
    public void Replace(string id, int version) => Assert.Equal(WriteOutcome.Written, _writer.Replace(Record(id), version, Content(id, version + 1)).Outcome);

    /// <summary>Writes a document of any other kind over its <paramref name="version"/>, such as a user file that an admin changes (M4.5a).</summary>
    public void Replace(DocumentKey key, int version, JsonObject content) => Assert.Equal(WriteOutcome.Written, _writer.Replace(key, version, content).Outcome);

    private static JsonObject Content(string id, int version) => new() { ["name"] = $"Demo record {id}", ["note"] = $"Made up, version {version}" };
}

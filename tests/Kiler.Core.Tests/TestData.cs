using System.Text.Json.Nodes;
using Kiler.Storage;
using Microsoft.Win32.SafeHandles;

namespace Kiler.Core.Tests;

/// <summary>
/// A data folder and its local copy in a temporary folder (M4.1), so a test writes the content of a model through the data
/// layer, as Kiler does, and reads the <see cref="Document"/> back. The folder is deleted with it.
/// </summary>
internal sealed class TestData : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Kiler.Tests", Guid.NewGuid().ToString("N"));
    private readonly DataCopy _copy;
    private int _copies;

    public TestData()
    {
        var copy = Path.Combine(_folder, "copy");
        Directory.CreateDirectory(copy);
        _copy = DataCopy.Open(DataFolder.Create(Folder, new NoOwners()), copy, TimeProvider.System);
    }

    /// <summary>The full path of the data folder.</summary>
    public string Folder => Path.Combine(_folder, "data");

    /// <summary>The local copy that writes the documents of the test.</summary>
    public DataCopy Copy => _copy;

    /// <summary>
    /// Another local copy of the same data folder (M4.2), as the Kiler of another user has: what the test writes through
    /// <see cref="Copy"/> reaches it with its next <see cref="DataCopy.Load"/>.
    /// </summary>
    public DataCopy OpenCopy()
    {
        var copy = Path.Combine(_folder, $"copy{++_copies}");
        Directory.CreateDirectory(copy);
        return DataCopy.Open(DataFolder.Open(Folder, new NoOwners()), copy, TimeProvider.System);
    }

    /// <summary>Writes <paramref name="content"/> as a new document and gives it as the local copy reads it.</summary>
    public Document WriteAndRead(DocumentKey key, JsonObject content)
    {
        Assert.Equal(WriteOutcome.Written, _copy.Create(key, content).Outcome);
        var loaded = Assert.Single(_copy.Documents(), document => document.Key == key);
        return Assert.IsType<Document>(loaded.Read.Document);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>No owners: the models do not read them.</summary>
    private sealed class NoOwners : IFileOwner
    {
        public string? Of(SafeFileHandle file) => null;
    }
}

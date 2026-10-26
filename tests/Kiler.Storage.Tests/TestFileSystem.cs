namespace Kiler.Storage.Tests;

/// <summary>
/// The real file operations with hooks for the tests: a count of the requests for attributes, and actions just before a
/// folder is made or renamed, which can also throw to fail that step.
/// </summary>
internal sealed class TestFileSystem : IFileSystem
{
    public int AttributeRequests { get; set; }

    public Action<string>? BeforeCreate { get; set; }

    public Action<string, string>? BeforeMove { get; set; }

    public FileAttributes? Attributes(string path)
    {
        AttributeRequests++;
        return FileSystem.Instance.Attributes(path);
    }

    public IReadOnlyList<FileEntry> List(string folder) => FileSystem.Instance.List(folder);

    public void CreateFolder(string path)
    {
        BeforeCreate?.Invoke(path);
        FileSystem.Instance.CreateFolder(path);
    }

    public void MoveFolder(string from, string to)
    {
        BeforeMove?.Invoke(from, to);
        FileSystem.Instance.MoveFolder(from, to);
    }
}

namespace Kiler.Storage.Tests;

/// <summary>
/// The real file operations with hooks for the tests: a count of the requests for attributes and of the bytes read, actions
/// just before a step, which can throw to fail that step, a simulated crash, and the waits counted instead of waited.
/// </summary>
internal sealed class TestFileSystem : IFileSystem
{
    public int AttributeRequests { get; set; }

    public long BytesRead { get; set; }

    public Action<string>? BeforeCreate { get; set; }

    public Action<string, string>? BeforeMove { get; set; }

    /// <summary>
    /// Called before every operation with its name, such as <c>nameof(IFileSystem.MoveFile)</c>, and its path; it can throw
    /// to fail that step. <see cref="IFileSystem.SetCreationToWriteTime"/> gives the path where the file was made.
    /// </summary>
    public Action<string, string>? Before { get; set; }

    /// <summary>
    /// The process stops at the first operation for which this gives <c>true</c>, as a crash or a lost network stops it: that
    /// operation fails, a new file there is left with half of its bytes, and every later operation fails too, so nothing of
    /// the write runs after it.
    /// </summary>
    public Func<string, string, bool>? CrashAt { get; set; }

    public bool Crashed { get; private set; }

    /// <summary>How long a new file takes to write: its write time is moved on by this, as a slow network does.</summary>
    public TimeSpan WriteTakes { get; set; }

    /// <summary>
    /// Makes new files as a file server would that lets a create go through a folder made a mount point in place, which NTFS
    /// refuses for a create-new (measured in M2.3), so a test reaches the check of the held folders.
    /// </summary>
    public bool CreateThroughLinks { get; set; }

    /// <summary>The waits between the tries of a step, in order. None is waited.</summary>
    public List<TimeSpan> Waits { get; } = [];

    public FileAttributes? Attributes(string path)
    {
        Step(nameof(Attributes), path);
        AttributeRequests++;
        return FileSystem.Instance.Attributes(path);
    }

    public IReadOnlyList<FileEntry> List(string folder)
    {
        Step(nameof(List), folder);
        return FileSystem.Instance.List(folder);
    }

    public void CreateFolder(string path)
    {
        Step(nameof(CreateFolder), path);
        BeforeCreate?.Invoke(path);
        FileSystem.Instance.CreateFolder(path);
    }

    public void MoveFolder(string from, string to)
    {
        Step(nameof(MoveFolder), from);
        BeforeMove?.Invoke(from, to);
        FileSystem.Instance.MoveFolder(from, to);
    }

    public PinnedFolder HoldFolder(string path)
    {
        Step(nameof(HoldFolder), path);
        return FileSystem.Instance.HoldFolder(path);
    }

    public Stream? OpenRead(string path)
    {
        Step(nameof(OpenRead), path);
        return FileSystem.Instance.OpenRead(path) is { } stream ? new CountingStream(stream, read => BytesRead += read) : null;
    }

    public NewFile? CreateFile(string path, ReadOnlySpan<byte> bytes)
    {
        if (!Crashed && CrashAt?.Invoke(nameof(CreateFile), path) == true)
        {
            Crashed = true;
            FileSystem.Instance.CreateFile(path, bytes[..(bytes.Length / 2)])?.Dispose();
            throw new SimulatedCrash();
        }

        Step(nameof(CreateFile), path);
        if (CreateThroughLinks)
        {
            var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return new NewFile(path, stream);
        }

        var file = FileSystem.Instance.CreateFile(path, bytes);
        if (file is not null && WriteTakes > TimeSpan.Zero)
        {
            File.SetLastWriteTimeUtc(file.Handle, File.GetLastWriteTimeUtc(file.Handle) + WriteTakes);
        }

        return file;
    }

    public FileStamp SetCreationToWriteTime(NewFile file)
    {
        Step(nameof(SetCreationToWriteTime), file.Path);
        return FileSystem.Instance.SetCreationToWriteTime(file);
    }

    public bool MoveFile(string from, string to, bool replace)
    {
        Step(nameof(MoveFile), from);
        return FileSystem.Instance.MoveFile(from, to, replace);
    }

    public void DeleteFile(string path)
    {
        Step(nameof(DeleteFile), path);
        FileSystem.Instance.DeleteFile(path);
    }

    public void Wait(TimeSpan time)
    {
        Step(nameof(Wait), "");
        Waits.Add(time);
    }

    private void Step(string operation, string path)
    {
        if (Crashed)
        {
            throw new SimulatedCrash();
        }

        if (CrashAt?.Invoke(operation, path) == true)
        {
            Crashed = true;
            throw new SimulatedCrash();
        }

        Before?.Invoke(operation, path);
    }

    /// <summary>What a step that the test stops throws. An <see cref="IOException"/>, as a lost network gives.</summary>
    private sealed class SimulatedCrash() : IOException("The test stopped the process here.");
}

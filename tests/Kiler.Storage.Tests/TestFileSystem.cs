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
    /// to fail that step. <see cref="IFileSystem.SetCreationToWriteTime"/> and <see cref="IFileSystem.DeleteNewFile"/> give
    /// the path where the file was made, <see cref="IFileSystem.Stamp"/> none.
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

    /// <summary>
    /// Makes no markers, as for a file server that would let a folder that is not empty be made a mount point, which NTFS
    /// refuses (measured in M2.5), so a test reaches the checks after a move.
    /// </summary>
    public bool MarkersKeepNothing { get; set; }

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

    public PinnedFolder HoldOrCreateFolder(string path)
    {
        Step(nameof(HoldOrCreateFolder), path);
        return FileSystem.Instance.HoldOrCreateFolder(path);
    }

    public Stream? OpenRead(string path)
    {
        Step(nameof(OpenRead), path);
        return FileSystem.Instance.OpenRead(path) is { } stream ? new CountingStream(stream, read => BytesRead += read) : null;
    }

    public FileStamp Stamp(Stream file)
    {
        Step(nameof(Stamp), "");
        return FileSystem.Instance.Stamp(file is CountingStream counting ? counting.Inner : file);
    }

    public FileTimes? OpenTimes(string path)
    {
        Step(nameof(OpenTimes), path);
        return FileSystem.Instance.OpenTimes(path);
    }

    /// <summary><see cref="IFileSystem.KeepCreationTime"/>; the step gives the path where the file was opened.</summary>
    public void KeepCreationTime(FileTimes file)
    {
        Step(nameof(KeepCreationTime), file.Path);
        FileSystem.Instance.KeepCreationTime(file);
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

    /// <summary><see cref="IFileSystem.DeleteNewFile"/>; the step gives the path where the file was made.</summary>
    public void DeleteNewFile(NewFile file)
    {
        Step(nameof(DeleteNewFile), file.Path);
        FileSystem.Instance.DeleteNewFile(file);
    }

    public HeldFile? HoldFile(string path)
    {
        Step(nameof(HoldFile), path);
        return FileSystem.Instance.HoldFile(path);
    }

    public void DeleteHeldFile(HeldFile file)
    {
        Step(nameof(DeleteHeldFile), file.Path);
        FileSystem.Instance.DeleteHeldFile(file);
    }

    public IDisposable? CreateMarker(string path)
    {
        Step(nameof(CreateMarker), path);
        return MarkersKeepNothing ? new NoMarker() : FileSystem.Instance.CreateMarker(path);
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

    private sealed class NoMarker : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

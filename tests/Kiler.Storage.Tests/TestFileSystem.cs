using Microsoft.Win32.SafeHandles;

namespace Kiler.Storage.Tests;

/// <summary>
/// The real file operations with hooks for the tests: a count of the requests for attributes, of the files opened to read and
/// of the bytes read, actions just before a step, which can throw to fail that step, a simulated crash, and the waits counted
/// instead of waited. The owners come from a fake (<see cref="Owners"/>), through the handles of the real operations. The
/// counts hold when the operations run on many threads at once, as the reads of a load do (M2.7); the hooks are called on
/// those threads.
/// </summary>
internal sealed class TestFileSystem : IFileSystem
{
    private readonly FileSystem _real;
    private int _attributeRequests;
    private long _bytesRead;
    private int _reads;
    private int _readsNow;
    private int _mostReadsAtOnce;

    public TestFileSystem()
        : this(new TestOwners())
    {
    }

    /// <summary>
    /// The file operations of another process on the same files, with the same owners, such as the Kiler of another user
    /// (M2.9c), whose new files and folders get its own SID (<see cref="Maker"/>).
    /// </summary>
    public TestFileSystem(TestOwners owners)
    {
        Owners = owners;
        _real = new FileSystem(Owners);
    }

    /// <summary>The owners that the files and folders have, by default <see cref="TestOwners.Me"/>.</summary>
    public TestOwners Owners { get; }

    /// <summary>
    /// The owner of the files and folders that the layer makes through this file system, as Windows gives them the SID of the
    /// process that makes them; <c>null</c> gives them none of their own, so they have <see cref="TestOwners.Default"/>. A
    /// marker gets none, as no test reads its owner.
    /// </summary>
    public string? Maker { get; init; }

    public int AttributeRequests
    {
        get => Volatile.Read(ref _attributeRequests);
        set => Volatile.Write(ref _attributeRequests, value);
    }

    public long BytesRead
    {
        get => Interlocked.Read(ref _bytesRead);
        set => Interlocked.Exchange(ref _bytesRead, value);
    }

    /// <summary>The files that <see cref="IFileSystem.OpenRead"/> opened.</summary>
    public int Reads
    {
        get => Volatile.Read(ref _reads);
        set => Volatile.Write(ref _reads, value);
    }

    /// <summary>
    /// The most reads at once: each counts from the call of <see cref="IFileSystem.OpenRead"/>, its <see cref="Before"/>
    /// included, until its file is closed.
    /// </summary>
    public int MostReadsAtOnce
    {
        get => Volatile.Read(ref _mostReadsAtOnce);
        set => Volatile.Write(ref _mostReadsAtOnce, value);
    }

    public Action<string>? BeforeCreate { get; set; }

    public Action<string, string>? BeforeMove { get; set; }

    /// <summary>
    /// Called before every operation with its name, such as <c>nameof(IFileSystem.MoveFile)</c>, and its path; it can throw
    /// to fail that step. <see cref="IFileSystem.SetCreationToWriteTime"/> and <see cref="IFileSystem.DeleteNewFile"/> give
    /// the path where the file was made, the owner of a held folder the path where it was held, and
    /// <see cref="IFileSystem.Stamp"/> and the owner of a file that was opened to read none.
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

    /// <summary>
    /// Gives every folder the index 0, as a file system without a unique index may (M2.6), so a test sees that an id that tells
    /// nothing is refused.
    /// </summary>
    public bool ZeroIds { get; set; }

    /// <summary>
    /// Lists every file with the size 0, as a listing may give a size that is not current (M2.9b), so a test sees that a size
    /// the layer acts on is read through a handle.
    /// </summary>
    public bool ListsNoSizes { get; set; }

    /// <summary>The waits between the tries of a step, in order. None is waited.</summary>
    public List<TimeSpan> Waits { get; } = [];

    public FileAttributes? Attributes(string path)
    {
        Step(nameof(Attributes), path);
        Interlocked.Increment(ref _attributeRequests);
        return _real.Attributes(path);
    }

    public IReadOnlyList<FileEntry> List(string folder, int limit = int.MaxValue)
    {
        Step(nameof(List), folder);
        var entries = _real.List(folder, limit);
        return ListsNoSizes ? [.. entries.Select(entry => entry with { Size = 0 })] : entries;
    }

    public void CreateFolder(string path)
    {
        Step(nameof(CreateFolder), path);
        BeforeCreate?.Invoke(path);
        _real.CreateFolder(path);
        if (Maker is not null)
        {
            Owners.Set(path, Maker);
        }
    }

    public void MoveFolder(string from, string to)
    {
        Step(nameof(MoveFolder), from);
        BeforeMove?.Invoke(from, to);
        _real.MoveFolder(from, to);
    }

    public PinnedFolder HoldFolder(string path, bool readOwner = false)
    {
        Step(nameof(HoldFolder), path);
        return _real.HoldFolder(path, readOwner);
    }

    public PinnedFolder HoldOrCreateFolder(string path, bool readOwner = false)
    {
        Step(nameof(HoldOrCreateFolder), path);
        var made = Maker is not null && !Path.Exists(path);
        var held = _real.HoldOrCreateFolder(path, readOwner);
        if (made)
        {
            Owners.Set(NativeFiles.Id(held.Handle), Maker);
        }

        return held;
    }

    /// <summary><see cref="IFileSystem.HoldNewFolder"/>, after <see cref="BeforeCreate"/> too, as it makes a folder.</summary>
    public PinnedFolder? HoldNewFolder(string path, bool readOwner = false)
    {
        Step(nameof(HoldNewFolder), path);
        BeforeCreate?.Invoke(path);
        var held = _real.HoldNewFolder(path, readOwner);
        if (held is not null && Maker is not null)
        {
            Owners.Set(NativeFiles.Id(held.Handle), Maker);
        }

        return held;
    }

    public FileId Id(PinnedFolder folder)
    {
        Step(nameof(Id), folder.HeldPath);
        var id = _real.Id(folder);
        return ZeroIds ? id with { Index = 0 } : id;
    }

    public Stream? OpenRead(string path)
    {
        var now = Interlocked.Increment(ref _readsNow);
        var most = Volatile.Read(ref _mostReadsAtOnce);
        while (now > most)
        {
            var seen = Interlocked.CompareExchange(ref _mostReadsAtOnce, now, most);
            if (seen == most)
            {
                break;
            }

            most = seen;
        }

        Stream? stream = null;
        try
        {
            Step(nameof(OpenRead), path);
            stream = _real.OpenRead(path);
        }
        finally
        {
            if (stream is null)
            {
                Interlocked.Decrement(ref _readsNow);
            }
        }

        if (stream is null)
        {
            return null;
        }

        Interlocked.Increment(ref _reads);
        return new CountingStream(stream, read => Interlocked.Add(ref _bytesRead, read), onDispose: () => Interlocked.Decrement(ref _readsNow));
    }

    public FileStamp Stamp(Stream file)
    {
        Step(nameof(Stamp), "");
        return _real.Stamp(file is CountingStream counting ? counting.Inner : file);
    }

    public string? Owner(Stream file)
    {
        Step(nameof(Owner), "");
        return _real.Owner(file is CountingStream counting ? counting.Inner : file);
    }

    public string? Owner(PinnedFolder folder)
    {
        Step(nameof(Owner), folder.HeldPath);
        return _real.Owner(folder);
    }

    public FileTimes? OpenTimes(string path)
    {
        Step(nameof(OpenTimes), path);
        return _real.OpenTimes(path);
    }

    /// <summary><see cref="IFileSystem.KeepCreationTime"/>; the step gives the path where the file was opened.</summary>
    public void KeepCreationTime(FileTimes file)
    {
        Step(nameof(KeepCreationTime), file.Path);
        _real.KeepCreationTime(file);
    }

    public NewFile? CreateFile(string path, ReadOnlySpan<byte> bytes)
    {
        if (!Crashed && CrashAt?.Invoke(nameof(CreateFile), path) == true)
        {
            Crashed = true;
            _real.CreateFile(path, bytes[..(bytes.Length / 2)])?.Dispose();
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

        var file = _real.CreateFile(path, bytes);
        if (file is not null && WriteTakes > TimeSpan.Zero)
        {
            File.SetLastWriteTimeUtc(file.Handle, File.GetLastWriteTimeUtc(file.Handle) + WriteTakes);
        }

        if (file is not null && Maker is not null)
        {
            Owners.Set(NativeFiles.Id(file.Handle), Maker);
        }

        return file;
    }

    public FileStamp SetCreationToWriteTime(NewFile file)
    {
        Step(nameof(SetCreationToWriteTime), file.Path);
        return _real.SetCreationToWriteTime(file);
    }

    public bool MoveFile(string from, string to, bool replace)
    {
        Step(nameof(MoveFile), from);
        return _real.MoveFile(from, to, replace);
    }

    public void DeleteFile(string path)
    {
        Step(nameof(DeleteFile), path);
        _real.DeleteFile(path);
    }

    /// <summary><see cref="IFileSystem.DeleteNewFile"/>; the step gives the path where the file was made.</summary>
    public void DeleteNewFile(NewFile file)
    {
        Step(nameof(DeleteNewFile), file.Path);
        _real.DeleteNewFile(file);
    }

    public HeldFile? HoldFile(string path)
    {
        Step(nameof(HoldFile), path);
        return _real.HoldFile(path);
    }

    public void DeleteHeldFile(HeldFile file)
    {
        Step(nameof(DeleteHeldFile), file.Path);
        _real.DeleteHeldFile(file);
    }

    public PinnedFolder HoldFolderToDelete(string path)
    {
        Step(nameof(HoldFolderToDelete), path);
        return _real.HoldFolderToDelete(path);
    }

    /// <summary><see cref="IFileSystem.DeleteHeldFolder"/>; the step gives the path where the folder was held.</summary>
    public void DeleteHeldFolder(PinnedFolder folder)
    {
        Step(nameof(DeleteHeldFolder), folder.HeldPath);
        _real.DeleteHeldFolder(folder);
    }

    public IDisposable? CreateMarker(string path)
    {
        Step(nameof(CreateMarker), path);
        return MarkersKeepNothing ? new NoMarker() : _real.CreateMarker(path);
    }

    public SafeFileHandle OpenToAppend(string path)
    {
        Step(nameof(OpenToAppend), path);
        return _real.OpenToAppend(path);
    }

    /// <summary><see cref="IFileSystem.Append"/>; the step gives no path.</summary>
    public void Append(SafeFileHandle file, ReadOnlySpan<byte> bytes)
    {
        Step(nameof(Append), "");
        _real.Append(file, bytes);
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

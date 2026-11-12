namespace Kiler.App.Tests;

/// <summary>A file of the fake shared folder, and what its stream does.</summary>
internal sealed class SharedFile(byte[] bytes)
{
    public byte[] Bytes => bytes;

    public Exception? OpenError { get; set; }

    /// <summary>The length that its stream says, when it is not its own.</summary>
    public long? Length { get; set; }

    /// <summary>Thrown at the second read, so the copy has begun.</summary>
    public Exception? ReadError { get; set; }

    /// <summary>Done at the second read.</summary>
    public Action? OnSecondRead { get; set; }

    public long BytesRead { get; set; }
}

/// <summary>
/// A read into a span comes here too: <see cref="MemoryStream"/> gives the read of a span of a derived type to
/// <see cref="Stream"/>, which reads into an array; an override of both calls itself until the stack overflows.
/// </summary>
internal sealed class SharedStream(SharedFile file) : MemoryStream(file.Bytes, writable: false)
{
    private int _reads;

    public override long Length => file.Length ?? base.Length;

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (++_reads == 2)
        {
            file.OnSecondRead?.Invoke();
            if (file.ReadError is not null)
            {
                throw file.ReadError;
            }
        }

        var read = base.Read(buffer, offset, count);
        file.BytesRead += read;
        return read;
    }
}

/// <summary>The <c>.new</c> in memory; its bytes can be read after it is disposed.</summary>
internal sealed class NewFile : MemoryStream
{
    public bool Disposed { get; private set; }

    public bool FlushedToDisk { get; set; }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>
/// The files of the shared folder and of a copy of the exe in memory (<see cref="IUpdateFiles"/>), for the check of a new
/// version (<see cref="UpdateCheckTests"/>) and of the first copy (<see cref="LocalCopyTests"/>).
/// </summary>
internal sealed class FakeUpdateFiles : IUpdateFiles
{
    public Dictionary<string, SharedFile> Shared { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Opened { get; } = [];

    public Exception? CreateError { get; set; }

    public Dictionary<string, NewFile> Created { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each delete, and whether the file was still held then.</summary>
    public List<(string Path, bool Held)> Deleted { get; } = [];

    /// <summary>The deletes that fail, in order; the rest succeed.</summary>
    public Queue<bool> DeleteFails { get; } = new();

    public Stream OpenShared(string path)
    {
        Opened.Add(path);
        if (!Shared.TryGetValue(path, out var file))
        {
            throw new FileNotFoundException("No such file in the fake shared folder.", path);
        }

        if (file.OpenError is not null)
        {
            throw file.OpenError;
        }

        return new SharedStream(file);
    }

    public Stream CreateUpdate(string path)
    {
        if (CreateError is not null)
        {
            throw CreateError;
        }

        if (Existing.Contains(path))
        {
            throw new IOException("A file of that name is there.");
        }

        var file = new NewFile();
        Created[path] = file;
        Existing.Add(path);
        return file;
    }

    /// <summary>Thrown when the <c>.new</c> is written to the disk.</summary>
    public Exception? FlushError { get; set; }

    /// <summary>Thrown by every delete.</summary>
    public Exception? DeleteError { get; set; }

    /// <summary>Each rename, and whether the file was still held then.</summary>
    public List<(string From, string To, bool Held)> Renamed { get; } = [];

    /// <summary>Thrown by every rename of a file that is there to a free name.</summary>
    public Exception? RenameError { get; set; }

    public void FlushToDisk(Stream update)
    {
        if (FlushError is not null)
        {
            throw FlushError;
        }

        ((NewFile)update).FlushedToDisk = true;
    }

    public void Rename(string from, string to)
    {
        if (!Existing.Contains(from))
        {
            throw new FileNotFoundException("No such file in the fake folder.", from);
        }

        if (Existing.Contains(to))
        {
            throw new IOException("Cannot create a file when that file already exists.", unchecked((int)0x800700B7));
        }

        if (RenameError is not null)
        {
            throw RenameError;
        }

        Renamed.Add((from, to, Created.TryGetValue(from, out var file) && !file.Disposed));
        Existing.Remove(from);
        Existing.Add(to);
    }

    public bool TryDelete(string path)
    {
        if (DeleteError is not null)
        {
            throw DeleteError;
        }

        Deleted.Add((path, Created.TryGetValue(path, out var file) && !file.Disposed));
        if (DeleteFails.TryDequeue(out var fails) && fails)
        {
            return false;
        }

        Existing.Remove(path);
        return true;
    }
}

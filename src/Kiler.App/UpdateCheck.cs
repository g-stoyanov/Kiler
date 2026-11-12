using System.Security.Cryptography;
using Kiler.Core;
using Kiler.Crypto;

namespace Kiler.App;

/// <summary>
/// A new version copied next to the local copy and checked (M3.6). The <c>.new</c> stays open, so that no one can write it
/// until it is in place (M3.7); disposing lets it go and leaves the file. Its name is new for each check, so a rename by
/// that name moves this file and no other (security review of M3.6).
/// </summary>
internal sealed class CheckedUpdate(ReleaseVersion version, string path, Stream file) : IDisposable
{
    public ReleaseVersion Version => version;

    /// <summary><c>Kiler.exe.&lt;random&gt;.new</c> next to the local copy.</summary>
    public string Path => path;

    public void Dispose() => file.Dispose();
}

/// <summary>
/// The local copy looks for a new version (design 4.1, M3 step 11, M3.6). It runs in the background after the start
/// (decided by the user in M3.6), so a shared folder that does not answer holds up neither the start nor the stop. It reads
/// <c>version.json</c> of the shared folder that <c>share.json</c> remembers, and for a version higher than its own it
/// copies <c>Kiler.exe</c> of the shared folder next to itself as <c>Kiler.exe.&lt;random&gt;.new</c>. It checks the SHA-256 of that local
/// file and a signature of the version and that hash with the key built into the copy (<see cref="UpdateKey"/>), never a key
/// or the hash text from the shared folder, which anyone in the team can write. Anything else deletes the <c>.new</c> and
/// says so in one line, and Kiler goes on with its own version: nothing here ever stops it.
/// </summary>
internal sealed class UpdateCheck
{
    /// <summary>The ending of a new version next to the local copy: <c>Kiler.exe.&lt;random&gt;.new</c>.</summary>
    public const string NewEnding = ".new";

    private const int BufferSize = 81920;

    private readonly CopyPlace _place;
    private readonly ShareFile _share;
    private readonly IUpdateFiles _files;
    private readonly string _publicKey;
    private readonly string _ownVersion;
    private readonly TextWriter _output;
    private readonly TextWriter _errors;
    private readonly CancellationToken _stopping;
    private readonly long _maxExeSize;

    /// <param name="place">The local copy, whose folder alone gets the <c>.new</c>.</param>
    /// <param name="share"><c>share.json</c>, with the shared folder of the new versions.</param>
    /// <param name="files">The files of the shared folder and of the <c>.new</c>.</param>
    /// <param name="publicKey">The key built into the copy: <see cref="UpdateKey.PublicKey"/>, a key of a test in the tests.</param>
    /// <param name="ownVersion">The version of the running exe: <see cref="HealthApi.Version"/>.</param>
    /// <param name="output">Where what was done is said.</param>
    /// <param name="errors">Where what was refused is said.</param>
    /// <param name="stopping">Kiler stops: the check ends, and says nothing more.</param>
    public UpdateCheck(CopyPlace place, ShareFile share, IUpdateFiles files, string publicKey, string ownVersion, TextWriter output, TextWriter errors, CancellationToken stopping)
        : this(place, share, files, publicKey, ownVersion, output, errors, stopping, VersionFile.MaxExeSize)
    {
    }

    /// <summary>With a smaller limit of the exe, for the tests.</summary>
    internal UpdateCheck(CopyPlace place, ShareFile share, IUpdateFiles files, string publicKey, string ownVersion, TextWriter output, TextWriter errors, CancellationToken stopping, long maxExeSize)
    {
        _place = place;
        _share = share;
        _files = files;
        _publicKey = publicKey;
        _ownVersion = ownVersion;
        _output = output;
        _errors = errors;
        _stopping = stopping;
        _maxExeSize = maxExeSize;
    }

    /// <summary>
    /// A new name for the <c>.new</c> of a check, next to the local copy: an update is made only in its folder. Two Kilers of
    /// the user that check at once never share one, so one never deletes or renames the file of the other, and a file that is
    /// there is never taken over (security review of M3.6). M3.7 deletes the ones that no Kiler holds.
    /// </summary>
    public string NewPath() => $"{_place.ExePath}.{Guid.NewGuid():N}{NewEnding}";

    /// <summary>Runs the check on a task of its own; the task never fails.</summary>
    public Task StartInBackground() => Task.Run(
        () =>
        {
            using var update = Run();

            // Until the new version goes into place (M3.7), the checked .new is deleted while it is held: each check makes one
            // under a new name, so each start would leave one more (security review of M3.6).
            if (update is not null)
            {
                _files.TryDelete(update.Path);
            }
        },
        CancellationToken.None);

    /// <summary>The new version, copied and checked, or null. It never throws: a fault of the code is a line too.</summary>
    public CheckedUpdate? Run()
    {
        try
        {
            return Check();
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            SayError($"Kiler could not look for a new version: {ex}");
            return null;
        }
    }

    private CheckedUpdate? Check()
    {
        _stopping.ThrowIfCancellationRequested();

        // A copy that cannot tell its own version takes nothing, and never takes 0.0.0 for it, which would take any older
        // signed version (M3.4, security review).
        if (!ReleaseVersion.TryParse(_ownVersion, out var own))
        {
            SayError($"Kiler does not look for a new version: its own version {_ownVersion} is not major.minor.patch.");
            return null;
        }

        if (_share.TryRead() is not { } folder)
        {
            Say("Kiler does not look for a new version: it has not been started from the shared folder yet.");
            return null;
        }

        var versionPath = Path.Combine(folder, VersionFile.Name);
        if (ReadVersionFile(versionPath) is not { } release)
        {
            return null;
        }

        // The same version is the usual case, and says nothing.
        if (release.Version <= own)
        {
            return null;
        }

        // The read may have taken long on a slow share: a Kiler that stops meanwhile copies nothing.
        _stopping.ThrowIfCancellationRequested();
        var exePath = Path.Combine(folder, CopyPlace.ExeName);
        Say($"Kiler {release.Version} is in the shared folder. Kiler copies it to {_place.Folder} and checks it.");
        return CopyAndCheck(exePath, release);
    }

    /// <summary>The version file of the shared folder, or null with a line.</summary>
    private VersionFile? ReadVersionFile(string path)
    {
        byte[] bytes;
        try
        {
            using var stream = _files.OpenShared(path);

            // One byte more than the limit is enough to tell a file that is too big; its length is not trusted.
            bytes = ReadUpTo(stream, VersionFile.MaxSize + 1);
        }
        catch (FileNotFoundException)
        {
            Say($"The shared folder has no {VersionFile.Name}, so Kiler takes no new version.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SayError($"Kiler cannot read {path}, so it takes no new version now: {ex.Message}");
            return null;
        }

        if (!VersionFile.TryRead(bytes, out var file, out var invalid))
        {
            SayError($"{path} does not fit, so Kiler takes no new version: {invalid}");
            return null;
        }

        return file;
    }

    /// <summary>The checked copy of the exe of the shared folder, or null with a line and no <c>.new</c> left.</summary>
    private CheckedUpdate? CopyAndCheck(string exePath, VersionFile release)
    {
        var newPath = NewPath();

        // Set once this check has made its .new, so a file that it did not make is never deleted.
        Stream? target = null;
        var tooBig = $"{exePath} is larger than {_maxExeSize} bytes, so Kiler takes no new version.";
        try
        {
            using (var source = _files.OpenShared(exePath))
            {
                if (source.Length > _maxExeSize)
                {
                    SayError(tooBig);
                    return null;
                }

                target = _files.CreateUpdate(newPath);
                if (!Copy(source, target))
                {
                    SayError(tooBig);
                    return null;
                }
            }

            // The hash of the local file, read through the stream that no one else can write (M3.4).
            target.Position = 0;
            var sha256 = SHA256.HashData(target);
            if (!release.Signatures.Any(signature => UpdateSignature.Verify(_publicKey, release.Version, sha256, signature)))
            {
                SayError($"{exePath} is not the Kiler {release.Version} that {VersionFile.Name} signs, so Kiler keeps its version.");
                return null;
            }

            var update = new CheckedUpdate(release.Version, newPath, target);
            target = null;
            Say($"Kiler {release.Version} is copied and checked.");
            return update;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SayError($"Kiler cannot copy {exePath} to {_place.Folder}, so it takes no new version now: {ex.Message}");
            return null;
        }
        finally
        {
            if (target is not null)
            {
                // Deleted while it is still held, so its name is this check's file; an antivirus that reads it may refuse
                // the delete until it is let go, so it is tried once more after that. One that cannot be deleted stays, and
                // is never taken for checked: only a CheckedUpdate goes into place (M3.7).
                var deleted = _files.TryDelete(newPath);
                target.Dispose();
                if (!deleted)
                {
                    _files.TryDelete(newPath);
                }
            }
        }
    }

    /// <summary>Copies the exe; false when it has more bytes than the limit, whatever its length said.</summary>
    private bool Copy(Stream source, Stream target)
    {
        var buffer = new byte[BufferSize];
        long copied = 0;
        int read;
        while ((read = source.Read(buffer)) > 0)
        {
            _stopping.ThrowIfCancellationRequested();
            copied += read;
            if (copied > _maxExeSize)
            {
                return false;
            }

            target.Write(buffer, 0, read);
        }

        return true;
    }

    private static byte[] ReadUpTo(Stream stream, int limit)
    {
        var buffer = new byte[limit];
        var length = 0;
        int read;
        while (length < limit && (read = stream.Read(buffer, length, limit - length)) > 0)
        {
            length += read;
        }

        return buffer[..length];
    }

    private void Say(string line) => Write(_output, line);

    private void SayError(string line) => Write(_errors, line);

    /// <summary>Writes a line, unless Kiler stops, so no line comes after its last one.</summary>
    private void Write(TextWriter writer, string line)
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        try
        {
            writer.WriteLine(line);
        }
        catch (IOException)
        {
            // A console that has gone: the line is lost.
        }
    }
}

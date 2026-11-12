using System.Security.Cryptography;
using Kiler.Core;
using Kiler.Crypto;

namespace Kiler.App;

/// <summary>What a read of <c>version.json</c> gave (<see cref="ReleaseCheck.ReadVersionFile"/>).</summary>
internal abstract record VersionFileRead
{
    private VersionFileRead()
    {
    }

    /// <summary>The file, in its format. Its signatures are not checked yet: only the copy of the exe can be.</summary>
    public sealed record Read(VersionFile File) : VersionFileRead;

    /// <summary>The shared folder has no <c>version.json</c>.</summary>
    public sealed record Missing : VersionFileRead;

    /// <summary>The file cannot be read now, with the message of the error.</summary>
    public sealed record Unreadable(string Message) : VersionFileRead;

    /// <summary>The file is not a <c>version.json</c>, with the reason.</summary>
    public sealed record DoesNotFit(string Reason) : VersionFileRead;
}

/// <summary>What a copy and check of an exe gave (<see cref="ReleaseCheck.CopyAndCheck"/>). Only <see cref="Checked"/> leaves the copy.</summary>
internal abstract record CopyCheck
{
    private CopyCheck()
    {
    }

    /// <summary>
    /// The copy is the exe that <c>version.json</c> signs, and it is on the disk. Its stream is still open, so no one else can
    /// write the file until it is in place; the caller disposes it.
    /// </summary>
    public sealed record Checked(Stream File) : CopyCheck;

    /// <summary>The exe has more bytes than the limit, by its length or by the bytes copied.</summary>
    public sealed record TooBig : CopyCheck;

    /// <summary>No signature of <c>version.json</c> verifies the version and the SHA-256 of the copy with the built-in key.</summary>
    public sealed record NotSigned : CopyCheck;

    /// <summary>The exe cannot be copied now, with the message of the error.</summary>
    public sealed record Failed(string Message) : CopyCheck;
}

/// <summary>
/// The check of an exe of the shared folder against the signed <c>version.json</c> next to it, which the local copy makes
/// for a new version (M3.6) and the start from the shared folder for its first copy (M3.8). It reads <c>version.json</c>
/// whatever its length says, copies the exe to a file that it makes and holds, so no one else can write it, takes the
/// SHA-256 of that local file through the same stream, and checks it with a signature of the version and that hash by
/// the key built into the exe (<see cref="UpdateKey"/>), never a key or the hash text of the shared folder, which anyone in
/// the team can write. Which version it may take, and what it says, is the caller's.
/// </summary>
/// <param name="files">The files of the shared folder and of the copy.</param>
/// <param name="publicKey">The key built into the exe: <see cref="UpdateKey.PublicKey"/>, a key of a test in the tests.</param>
/// <param name="maxExeSize">The largest exe it copies: <see cref="VersionFile.MaxExeSize"/>, smaller in the tests.</param>
/// <param name="stopping">Kiler stops: the copy ends with <see cref="OperationCanceledException"/>.</param>
internal sealed class ReleaseCheck(IUpdateFiles files, string publicKey, long maxExeSize, CancellationToken stopping)
{
    private const int BufferSize = 81920;

    public long MaxExeSize => maxExeSize;

    /// <summary>The <c>version.json</c> at <paramref name="path"/>, read up to one byte more than its limit.</summary>
    public VersionFileRead ReadVersionFile(string path)
    {
        byte[] bytes;
        try
        {
            using var stream = files.OpenShared(path);

            // One byte more than the limit is enough to tell a file that is too big; its length is not trusted.
            bytes = ReadUpTo(stream, VersionFile.MaxSize + 1);
        }
        catch (FileNotFoundException)
        {
            return new VersionFileRead.Missing();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new VersionFileRead.Unreadable(ex.Message);
        }

        return VersionFile.TryRead(bytes, out var file, out var invalid)
            ? new VersionFileRead.Read(file)
            : new VersionFileRead.DoesNotFit(invalid);
    }

    /// <summary>
    /// Copies <paramref name="exePath"/> to <paramref name="copyPath"/>, a name that is not there yet, and checks the copy
    /// against <paramref name="release"/>. A copy that is not <see cref="CopyCheck.Checked"/> is deleted, also when this
    /// throws: a fault of the code, or <see cref="OperationCanceledException"/> once Kiler stops.
    /// </summary>
    public CopyCheck CopyAndCheck(string exePath, string copyPath, VersionFile release)
    {
        // Set once the copy is made, so a file that this check did not make is never deleted.
        Stream? target = null;
        try
        {
            using (var source = files.OpenShared(exePath))
            {
                if (source.Length > maxExeSize)
                {
                    return new CopyCheck.TooBig();
                }

                target = files.CreateUpdate(copyPath);
                if (!Copy(source, target))
                {
                    return new CopyCheck.TooBig();
                }
            }

            // The hash of the local file, read through the stream that no one else can write (M3.4).
            target.Position = 0;
            var sha256 = SHA256.HashData(target);
            if (!release.Signatures.Any(signature => UpdateSignature.Verify(publicKey, release.Version, sha256, signature)))
            {
                return new CopyCheck.NotSigned();
            }

            // On the disk before a rename gives it the name Kiler.exe: a crash after the rename must not leave an exe of the
            // right length without its bytes (security review of M3.7). Only a checked one, so a refusal writes nothing more.
            files.FlushToDisk(target);
            var checkedFile = new CopyCheck.Checked(target);
            target = null;
            return checkedFile;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CopyCheck.Failed(ex.Message);
        }
        finally
        {
            if (target is not null)
            {
                Delete(copyPath, target);
            }
        }
    }

    /// <summary>
    /// Deletes a copy that <paramref name="file"/> still holds, so its name is that copy and no other file, then lets it go.
    /// An antivirus that reads it may refuse the delete until then, so it is tried once more after that. One that cannot be
    /// deleted stays, and is never taken for checked: only a checked copy takes the name <c>Kiler.exe</c>.
    /// </summary>
    public void Delete(string copyPath, Stream file)
    {
        var deleted = files.TryDelete(copyPath);
        file.Dispose();
        if (!deleted)
        {
            files.TryDelete(copyPath);
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
            stopping.ThrowIfCancellationRequested();
            copied += read;
            if (copied > maxExeSize)
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
}

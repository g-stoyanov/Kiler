using System.Text.Json;

namespace Kiler.App;

/// <summary>A running Kiler as the next start sees it: its port on 127.0.0.1 and its token.</summary>
internal sealed record RunningInstance(int Port, AccessToken Token)
{
    /// <summary>
    /// <c>http://127.0.0.1:port</c>, never <c>localhost</c>: Kiler does not hold <c>[::1]</c> on its port, and another
    /// process, even of another user, could listen there and get the token.
    /// </summary>
    public Uri Server => new($"http://127.0.0.1:{Port}/");

    public Uri EntryAddress => AccessGuard.EntryAddress(Server, Token);

    /// <summary>Only the port: the record would otherwise print <see cref="EntryAddress"/>, which holds the token.</summary>
    public override string ToString() => $"{nameof(RunningInstance)} {{ Port = {Port} }}";
}

/// <summary>The JSON of the instance file.</summary>
internal sealed record InstanceFileContent(int Port, string? Token)
{
    /// <summary>Only the port: the record would otherwise print the token.</summary>
    public override string ToString() => $"{nameof(InstanceFileContent)} {{ Port = {Port} }}";
}

/// <summary>
/// The instance file in <c>%LOCALAPPDATA%\Kiler</c>: the port and the token of the running Kiler, so that a second start
/// can open the browser to it. It is trusted only while the instance lock is held (<see cref="SingleInstance"/>).
/// </summary>
internal sealed class InstanceFile(string path)
{
    public string Path => path;

    /// <summary>
    /// Writes the file, and deletes it when the host starts to stop. The host raises <paramref name="stopping"/> before
    /// Kestrel lets the port go, so no start finds the file while the port is free and the lock is still held.
    /// </summary>
    public void WriteUntilStopping(RunningInstance instance, CancellationToken stopping)
    {
        stopping.Register(() => DeleteIfOwn(instance));
        Write(instance);
    }

    /// <summary>Writes the file through a temporary file and a rename, so that a second start never reads half of it.</summary>
    public void Write(RunningInstance instance)
    {
        // The path is always a file in a folder: %LOCALAPPDATA%\Kiler or a test folder.
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var content = new InstanceFileContent(instance.Port, instance.Token.Value);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(content, KilerJsonContext.Default.InstanceFileContent));
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temporary);
            throw;
        }
    }

    /// <summary>The instance from the file; null when there is no file, it cannot be read now, or it is not valid.</summary>
    public RunningInstance? TryRead()
    {
        InstanceFileContent? content;
        try
        {
            // A reader must not stop the lock holder from deleting or replacing the file.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            content = JsonSerializer.Deserialize(stream, KilerJsonContext.Default.InstanceFileContent);
        }
        // A missing file or folder is an IOException too.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }

        return content is { Port: >= 1 and <= 65535 } && AccessToken.TryParse(content.Token, out var token)
            ? new RunningInstance(content.Port, token)
            : null;
    }

    /// <summary>Deletes the file; true when it is gone, also when there was none.</summary>
    public bool TryDelete() => TryDeleteFile(path);

    /// <summary>Deletes the file only when it still describes this instance.</summary>
    public void DeleteIfOwn(RunningInstance instance)
    {
        if (TryRead() is { } current && current.Port == instance.Port && instance.Token.Matches(current.Token.Value))
        {
            TryDelete();
        }
    }

    private static bool TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        // A missing file is no error, but a missing folder is: on the first start there is no Kiler folder yet.
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

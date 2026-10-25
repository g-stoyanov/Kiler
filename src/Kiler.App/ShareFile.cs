using System.Text.Json;

namespace Kiler.App;

/// <summary>The JSON of the share file.</summary>
internal sealed record ShareFileContent(string? Folder);

/// <summary>
/// <c>%LOCALAPPDATA%\Kiler\share.json</c>: the shared folder that Kiler was last started from. Its <c>data\</c> is the default
/// data folder (M2), and the updates come from it (M3). Every start from a shared folder writes it again, so a shared folder
/// that has moved is taken with one start from its new place. The folder is kept as Kiler was started: a mapped drive stays
/// a drive, not its UNC path.
/// </summary>
internal sealed class ShareFile(string path)
{
    public string Path => path;

    public static ShareFile ForCurrentUser() => new(System.IO.Path.Combine(UserFolders.Kiler(), "share.json"));

    public void Write(string folder) =>
        PrivateFolder.WriteFile(path, JsonSerializer.SerializeToUtf8Bytes(new ShareFileContent(folder), KilerJsonContext.Default.ShareFileContent));

    /// <summary>The shared folder; null when there is no file, it cannot be read now, or it does not hold a full path.</summary>
    public string? TryRead()
    {
        ShareFileContent? content;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            content = JsonSerializer.Deserialize(stream, KilerJsonContext.Default.ShareFileContent);
        }
        // A missing file or folder is an IOException too.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }

        return content?.Folder is { } folder && System.IO.Path.IsPathFullyQualified(folder) ? folder : null;
    }
}

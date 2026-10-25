using Microsoft.Extensions.FileProviders;

namespace Kiler.App.Tests;

/// <summary>
/// UI files laid out as a Vite build lays them out, in a temporary folder: the page and the icon in the root, the files
/// with a hash in their names in <c>assets/</c>. The tests of the serving do not depend on a Release build of the real UI.
/// </summary>
internal sealed class MadeUpUi : IDisposable
{
    public const string Page = "<!doctype html><html><head><title>Made-up Kiler</title></head><body><div id=\"root\"></div></body></html>";
    public const string Script = "/assets/index-AbC123_-.js";
    public const string Worker = "/assets/events-worker-XyZ789-_.js";
    public const string Style = "/assets/index-DeF456.css";
    public const string Icon = "/favicon.svg";

    private readonly TempFolder _folder = new();
    private readonly PhysicalFileProvider _files;

    public MadeUpUi()
    {
        Write("/index.html", Page);
        Write(Script, "console.log('page');");
        Write(Worker, "console.log('worker');");
        Write(Style, "body { margin: 0; }");
        Write(Icon, "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        _files = new PhysicalFileProvider(_folder.Path);
    }

    public IFileProvider Files => _files;

    public void Dispose()
    {
        _files.Dispose();
        _folder.Dispose();
    }

    private void Write(string path, string content)
    {
        var file = Path.Combine(_folder.Path, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }
}

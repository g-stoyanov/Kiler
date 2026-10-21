using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Kiler.Probe;

/// <summary>
/// Writes each line to the console and saves all of them to a file next to the exe.
/// The saved file has the folder paths replaced, because it may stay on a share that others read.
/// </summary>
internal sealed class Report
{
    private readonly StringBuilder text = new();
    private readonly List<(string Value, string Placeholder)> hidden = [];
    private readonly string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

    public Report(string mode)
    {
        Hide(AppContext.BaseDirectory, "<exe folder>");
        Hide(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<profile>");
        Hide(Environment.UserName, "<user>");
        Line($"Kiler.Probe, mode {mode}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line($"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}");
        Line($"Exe location: {ProbeInfo.Location(AppContext.BaseDirectory)}");
    }

    public bool Failed { get; private set; }

    /// <summary>The saved file shows <paramref name="placeholder"/> in place of <paramref name="value"/>, a folder or a name.</summary>
    public void Hide(string value, string placeholder)
    {
        value = Path.TrimEndingDirectorySeparator(value);
        if (value.Length > 0)
        {
            hidden.Add((value, placeholder));
        }
    }

    public void Line(string line)
    {
        Console.WriteLine(line);
        text.AppendLine(line);
    }

    public void Check(string what, bool ok, string? detail = null)
    {
        Failed |= !ok;
        Line($"[{(ok ? "OK" : "FAIL")}] {what}{(detail is null ? "" : $": {detail}")}");
    }

    public void Timing(string what, int files, TimeSpan elapsed) =>
        Line($"{what}: {files} files in {ProbeInfo.Ms(elapsed)} ({elapsed.TotalMilliseconds / files:F2} ms per file)");

    public void Save()
    {
        var content = text.ToString();
        // The longest first, so that a folder inside the profile is not cut by the profile path.
        foreach (var (value, placeholder) in hidden.OrderByDescending(item => item.Value.Length))
        {
            content = content.Replace(value, placeholder, StringComparison.OrdinalIgnoreCase);
        }

        // A random suffix and CreateNew: never write through a file that someone else put there first.
        var path = Path.Combine(AppContext.BaseDirectory, $"Kiler.Probe-report-{stamp}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}.txt");
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(Encoding.UTF8.GetBytes(content));
            Console.WriteLine($"Report saved: {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Could not save the report next to the exe: {ex.Message}");
        }
    }
}

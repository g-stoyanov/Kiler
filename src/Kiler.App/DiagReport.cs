using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Kiler.App;

/// <summary>
/// The report of <c>--diag</c>: each line goes to the console at once, and all of them go into a file at the end. The file
/// may stay on the shared folder, which everyone can read, so it shows placeholders where the console shows folders and
/// account names.
/// </summary>
internal sealed class DiagReport(TextWriter console)
{
    private readonly StringBuilder _text = new();
    private readonly List<(string Value, string Placeholder, bool WholeWord)> _hidden = [];

    /// <summary>True once a check has failed.</summary>
    public bool Failed { get; private set; }

    /// <summary>The file shows <paramref name="placeholder"/> in place of the folder <paramref name="path"/>, in any letter case.</summary>
    public void HidePath(string path, string placeholder) => Hide(Path.TrimEndingDirectorySeparator(path), placeholder, wholeWord: false);

    /// <summary>
    /// The file shows <paramref name="placeholder"/> in place of <paramref name="name"/>, in any letter case, where it is not
    /// part of a longer word: a short account name such as "file" must not cut "files".
    /// </summary>
    public void HideName(string name, string placeholder) => Hide(name, placeholder, wholeWord: true);

    public void Line(string line)
    {
        console.WriteLine(line);
        _text.AppendLine(line);
    }

    public void Check(string what, bool ok, string? detail = null)
    {
        Failed |= !ok;
        Line($"[{(ok ? "OK" : "FAIL")}] {what}{(detail is null ? "" : $": {detail}")}");
    }

    /// <summary>A line with the time of a step and the time per file, with a decimal point in every culture.</summary>
    public void Timing(string what, int files, TimeSpan elapsed) => Line(files == 0
        ? $"{what}: no files in {Ms(elapsed)}"
        : string.Create(CultureInfo.InvariantCulture, $"{what}: {files} files in {Ms(elapsed)} ({elapsed.TotalMilliseconds / files:F2} ms per file)"));

    public static string Ms(TimeSpan time) => string.Create(CultureInfo.InvariantCulture, $"{time.TotalMilliseconds:F0} ms");

    /// <summary>
    /// The text of the file. All the values are replaced in one pass, the longest first, so a folder inside the profile is
    /// not cut by the profile, and a placeholder is never replaced again. Of two equal values the later one wins: the test
    /// folder can be the folder of the exe.
    /// </summary>
    public string ForFile()
    {
        var text = _text.ToString();
        var hidden = _hidden.Select((item, order) => (Item: item, Order: order))
            .OrderByDescending(entry => entry.Item.Value.Length)
            .ThenByDescending(entry => entry.Order)
            .Select(entry => entry.Item)
            .ToList();
        if (hidden.Count == 0)
        {
            return text;
        }

        // One group per value, so the placeholder comes from the group that matched, not from a lookup of the matched text.
        var pattern = string.Join('|', hidden.Select(item => item.WholeWord
            ? $@"(?<![\p{{L}}\p{{N}}])({Regex.Escape(item.Value)})(?![\p{{L}}\p{{N}}])"
            : $"({Regex.Escape(item.Value)})"));
        return Regex.Replace(
            text,
            pattern,
            match => hidden[Enumerable.Range(0, hidden.Count).First(i => match.Groups[i + 1].Success)].Placeholder,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Saves <see cref="ForFile"/> into <paramref name="folder"/> as <c>Kiler-diag-&lt;time&gt;-&lt;hex&gt;.txt</c> and says
    /// where on the console; null when it cannot. The random part and <see cref="FileMode.CreateNew"/>: never write through
    /// a file that someone else put there first.
    /// </summary>
    public string? Save(string folder, DateTimeOffset now)
    {
        var name = string.Create(CultureInfo.InvariantCulture, $"Kiler-diag-{now:yyyyMMdd-HHmmss}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}.txt");
        var path = Path.Combine(folder, name);
        var content = Encoding.UTF8.GetBytes(ForFile());
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            console.WriteLine($"Kiler could not save the report in {folder}: {ex.Message}");
            return null;
        }

        console.WriteLine($"Report saved: {path}");
        return path;
    }

    private void Hide(string value, string placeholder, bool wholeWord)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _hidden.Add((value, placeholder, wholeWord));
        }
    }
}

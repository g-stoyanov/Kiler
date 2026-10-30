using System.Diagnostics.CodeAnalysis;

namespace Kiler.DemoData;

/// <summary>The arguments of the demo data: the data folder to make, whether to replace one that is there, and the volume mode.</summary>
internal sealed record DemoOptions(string DataFolder, bool Replace, bool Volume)
{
    public const string Usage = """
        Makes the demo data: a made-up Kiler in a new data folder, with demo users and their demo passwords, stashes and
        records.

        Usage: dotnet run --project tools\Kiler.DemoData -- <data folder> [--replace] [--volume]

          <data folder>  The folder to make; the folder above it must exist. The dev-data folder of the repository is the
                         one of npm run dev.
          --replace      Delete the data folder and its local copy in %LOCALAPPDATA%\Kiler\copies first, when they are
                         there: only on a local disk, only a folder that holds nothing but a data folder, and not while a
                         Kiler runs on it.
          --volume       Make 2000 records in all, for timing the search: the demo records, and made-up ones of 40 more
                         systems.
          --help         Show this text.
        """;

    public static bool TryParse(IReadOnlyList<string> args, [NotNullWhen(true)] out DemoOptions? options, [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = null;
        string? folder = null;
        var flags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var arg in args)
        {
            if (arg is "--replace" or "--volume")
            {
                if (!flags.Add(arg))
                {
                    error = $"{arg} is given twice.";
                    return false;
                }
            }
            else if (arg.StartsWith('-'))
            {
                error = $"Unknown option: {arg}";
                return false;
            }
            else if (folder is not null)
            {
                error = "Give one data folder.";
                return false;
            }
            else
            {
                folder = arg;
            }
        }

        if (string.IsNullOrWhiteSpace(folder))
        {
            error = "Give the data folder.";
            return false;
        }

        options = new(folder, flags.Contains("--replace"), flags.Contains("--volume"));
        error = null;
        return true;
    }
}

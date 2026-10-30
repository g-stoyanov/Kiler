using System.Diagnostics.CodeAnalysis;

namespace Kiler.DemoData;

/// <summary>The arguments of the demo data: the data folder to make, and whether to replace one that is there.</summary>
internal sealed record DemoOptions(string DataFolder, bool Replace)
{
    public const string Usage = """
        Makes the demo data: a made-up Kiler in a new data folder, with demo users and their demo passwords.

        Usage: dotnet run --project tools\Kiler.DemoData -- <data folder> [--replace]

          <data folder>  The folder to make; the folder above it must exist. The dev-data folder of the repository is the
                         one of npm run dev.
          --replace      Delete the data folder and its local copy in %LOCALAPPDATA%\Kiler\copies first, when they are
                         there: only on a local disk, only a folder that holds nothing but a data folder, and not while a
                         Kiler runs on it.
          --help         Show this text.
        """;

    public static bool TryParse(IReadOnlyList<string> args, [NotNullWhen(true)] out DemoOptions? options, [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = null;
        string? folder = null;
        var replace = false;
        foreach (var arg in args)
        {
            if (arg == "--replace" && !replace)
            {
                replace = true;
            }
            else if (arg.StartsWith('-'))
            {
                error = arg == "--replace" ? "--replace is given twice." : $"Unknown option: {arg}";
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

        options = new(folder, replace);
        error = null;
        return true;
    }
}

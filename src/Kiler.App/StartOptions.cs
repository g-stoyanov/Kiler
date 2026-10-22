using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Kiler.App;

internal enum StartMode
{
    Server,
    Diag,
    Help,
}

/// <summary>The command line of Kiler.exe.</summary>
internal sealed record StartOptions
{
    public const string Usage = """
        Usage:
          Kiler.exe [--no-browser] [--port <port>] [--data <folder>]
          Kiler.exe --diag [<folder>] [--data <folder>]
          Kiler.exe --help

          --no-browser      do not open the browser; print the address instead
          --port <port>     listen on this port (1-65535) instead of a free one
          --data <folder>   use this data folder instead of the default one
          --diag [<folder>] measure the file access in a folder, by default the data folder
        """;

    public StartMode Mode { get; init; } = StartMode.Server;

    public bool NoBrowser { get; init; }

    /// <summary>The port from <c>--port</c>; null means a free port.</summary>
    public int? Port { get; init; }

    /// <summary>The folder from <c>--data</c>, as given; null means the default data folder.</summary>
    public string? DataFolder { get; init; }

    /// <summary>The folder after <c>--diag</c>, as given; null means the data folder.</summary>
    public string? DiagFolder { get; init; }

    /// <summary>Reads the arguments. An unknown or repeated argument, or a missing or invalid value, is an error.</summary>
    public static bool TryParse(
        IReadOnlyList<string> args,
        [NotNullWhen(true)] out StartOptions? options,
        [NotNullWhen(false)] out string? error)
    {
        options = null;
        var result = new StartOptions();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (!seen.Add(name))
            {
                error = $"{name} is given more than once.";
                return false;
            }

            switch (name)
            {
                case "--help" or "-h" or "/?":
                    options = new StartOptions { Mode = StartMode.Help };
                    error = null;
                    return true;

                case "--no-browser":
                    result = result with { NoBrowser = true };
                    break;

                case "--port":
                    if (!TryTakeValue(args, ref i, out var portText)
                        || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                        || port is < 1 or > 65535)
                    {
                        error = "--port needs a number from 1 to 65535.";
                        return false;
                    }

                    result = result with { Port = port };
                    break;

                case "--data":
                    if (!TryTakeValue(args, ref i, out var dataFolder))
                    {
                        error = "--data needs a folder.";
                        return false;
                    }

                    result = result with { DataFolder = dataFolder };
                    break;

                case "--diag":
                    // The folder is optional, so the next argument is taken only when it is not an option.
                    TryTakeValue(args, ref i, out var diagFolder);
                    result = result with { Mode = StartMode.Diag, DiagFolder = diagFolder };
                    break;

                default:
                    error = $"Unknown argument: {name}";
                    return false;
            }
        }

        if (result.Mode == StartMode.Diag && (result.NoBrowser || result.Port is not null))
        {
            error = "--diag starts no server, so it does not take --no-browser or --port.";
            return false;
        }

        options = result;
        error = null;
        return true;
    }

    /// <summary>
    /// The arguments for another Kiler.exe that starts the same server: the local copy (<see cref="LocalCopy"/>). The data
    /// folder becomes a full path, because the copy runs in another working folder, and the instance names come from it.
    /// </summary>
    public IReadOnlyList<string> ToServerArguments()
    {
        if (Mode != StartMode.Server)
        {
            throw new InvalidOperationException($"Only a server start is passed on, not {Mode}.");
        }

        var arguments = new List<string>();
        if (NoBrowser)
        {
            arguments.Add("--no-browser");
        }

        if (Port is { } port)
        {
            arguments.AddRange(["--port", port.ToString(CultureInfo.InvariantCulture)]);
        }

        if (DataFolder is { } dataFolder)
        {
            arguments.AddRange(["--data", Path.GetFullPath(dataFolder)]);
        }

        return arguments;
    }

    /// <summary>Takes the argument after <paramref name="i"/> as a value, unless it is missing, blank or looks like an option.</summary>
    private static bool TryTakeValue(IReadOnlyList<string> args, ref int i, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (i + 1 >= args.Count)
        {
            return false;
        }

        var next = args[i + 1];
        if (string.IsNullOrWhiteSpace(next) || next.StartsWith('-') || next == "/?")
        {
            return false;
        }

        value = next;
        i++;
        return true;
    }
}

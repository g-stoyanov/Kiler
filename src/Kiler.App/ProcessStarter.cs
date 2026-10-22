using System.Diagnostics;

namespace Kiler.App;

/// <summary>Starts a program. Tests replace it, so that nothing starts.</summary>
internal interface IProcessStarter
{
    /// <summary>Starts the program and does not wait for it.</summary>
    void Start(string program, IReadOnlyList<string> arguments);
}

internal sealed class ProcessStarter : IProcessStarter
{
    public void Start(string program, IReadOnlyList<string> arguments)
    {
        using var process = Process.Start(StartInfo(program, arguments));
    }

    /// <summary>
    /// Not through the shell, so exactly this program starts. Its output is not redirected: it writes to the same console,
    /// and this process neither reads nor keeps what it prints, the address with the token among it. Its own folder is
    /// its working folder, so it holds no handle on the shared folder.
    /// </summary>
    public static ProcessStartInfo StartInfo(string program, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(program),
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }
}

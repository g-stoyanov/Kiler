using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Kiler.App.Tests;

/// <summary>
/// A copy of the published Kiler.exe for the smoke tests, in a temporary folder of its own on the local disk, so it runs
/// there and does not copy itself into the profile (M1.5a). Each copy has a data folder of its own, so its runs are an
/// instance of their own and do not meet the user's Kiler (M1.5). Disposing it ends the runs that are left and deletes the
/// instance file that a killed run leaves in the real profile.
/// </summary>
internal sealed partial class KilerExe : IDisposable
{
    /// <summary>The environment variable with the path of the published exe. <c>smoke.ps1</c> sets it.</summary>
    public const string Variable = "KILER_SMOKE_EXE";

    /// <summary>An antivirus may still read the exe that has just run, so the folder is deleted in a few tries.</summary>
    private const int DeleteTries = 5;

    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly TempFolder _folder;
    private readonly List<KilerRun> _runs = [];

    public KilerExe()
    {
        var published = Published ?? throw new InvalidOperationException($"{Variable} is not set.");
        _folder = new TempFolder();
        try
        {
            ExePath = Path.Combine(_folder.Path, "Kiler.exe");
            File.Copy(published, ExePath);
            Assert.False(CopyFiles.IsNetworkPath(ExePath), "The copy of Kiler.exe must be on a local disk, or it copies itself into the profile.");
            DataFolder = Directory.CreateDirectory(Path.Combine(_folder.Path, "data")).FullName;
        }
        catch
        {
            _folder.Dispose();
            throw;
        }

        var place = InstancePlace.ForCurrentUser(DataFolder);
        InstanceFile = place.FilePath;
        CookieName = place.CookieName;
    }

    /// <summary>The published exe from <see cref="Variable"/>; null when it is not set.</summary>
    public static string? Published => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } path ? path : null;

    /// <summary>The copy of the exe.</summary>
    public string ExePath { get; }

    public string DataFolder { get; }

    /// <summary>The instance file of the runs, <c>%LOCALAPPDATA%\Kiler\instance-&lt;session&gt;-&lt;key&gt;.json</c> in the real profile.</summary>
    public string InstanceFile { get; }

    /// <summary>The name of the cookie of the runs, which comes from the data folder.</summary>
    public string CookieName { get; }

    /// <summary>
    /// Starts the copy with <c>--no-browser --data &lt;data folder&gt;</c> and then <paramref name="arguments"/>. A test never
    /// opens a browser. <paramref name="environment"/> is added to the environment of the test.
    /// </summary>
    public KilerRun Start(IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(ExePath)
        {
            WorkingDirectory = _folder.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in (string[])["--no-browser", "--data", DataFolder, .. arguments])
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        var run = new KilerRun(start);
        _runs.Add(run);
        return run;
    }

    /// <summary>
    /// The text as it may go into a test message: the value of every <c>t</c> parameter is hidden, and so is every word of
    /// 43 base64url characters, the form of the token, such as the value of a cookie.
    /// </summary>
    public static string MaskToken(string text) => TokenWord().Replace(TokenParameter().Replace(text, "$1…"), "…");

    public void Dispose()
    {
        try
        {
            foreach (var run in _runs)
            {
                run.Dispose();
            }
        }
        finally
        {
            // A killed run leaves its file, and the next start would only delete it.
            if (File.Exists(InstanceFile))
            {
                File.Delete(InstanceFile);
            }

            DeleteFolder();
        }
    }

    private void DeleteFolder()
    {
        for (var tries = 1; Directory.Exists(_folder.Path); tries++)
        {
            try
            {
                _folder.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && tries < DeleteTries)
            {
                Thread.Sleep(DeleteRetryDelay);
            }
        }
    }

    [GeneratedRegex(@"([?&]t=)[^&#\s]*")]
    private static partial Regex TokenParameter();

    [GeneratedRegex(@"(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{43}(?![A-Za-z0-9_-])")]
    private static partial Regex TokenWord();
}

/// <summary>One run of <see cref="KilerExe"/>: its output and errors line by line, and its end.</summary>
internal sealed class KilerRun : IDisposable
{
    private readonly Process _process;
    private readonly Lock _lock = new();
    private readonly List<string> _output = [];
    private readonly List<string> _errors = [];
    private readonly TaskCompletionSource _outputEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _errorsEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completed and replaced on every new line, so a wait for a line wakes up.</summary>
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _disposed;

    public KilerRun(ProcessStartInfo start)
    {
        _process = new Process { StartInfo = start };
        _process.OutputDataReceived += (_, e) => Add(_output, _outputEnded, e.Data);
        _process.ErrorDataReceived += (_, e) => Add(_errors, _errorsEnded, e.Data);
        Started = Stopwatch.GetTimestamp();
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    /// <summary>The time stamp taken just before the process started.</summary>
    public long Started { get; }

    public bool HasExited => _process.HasExited;

    /// <summary>
    /// The lines of the output so far, with the token hidden, because an assertion prints what it compares. The line with
    /// the token comes from <see cref="WaitForLineAsync"/>.
    /// </summary>
    public IReadOnlyList<string> Output
    {
        get
        {
            lock (_lock)
            {
                return [.. _output.Select(KilerExe.MaskToken)];
            }
        }
    }

    /// <summary>The lines of the errors so far, with the token hidden.</summary>
    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_lock)
            {
                return [.. _errors.Select(KilerExe.MaskToken)];
            }
        }
    }

    /// <summary>
    /// The first line of the output that starts with <paramref name="prefix"/>, as it was printed, the token too; fails when
    /// the output ends or the time is up first.
    /// </summary>
    public async Task<string> WaitForLineAsync(string prefix, TimeSpan timeout)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timer.CancelAfter(timeout);
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                if (_output.Find(line => line.StartsWith(prefix, StringComparison.Ordinal)) is { } found)
                {
                    return found;
                }

                if (_outputEnded.Task.IsCompleted)
                {
                    Assert.Fail($"Kiler.exe ended its output without a line that starts with \"{prefix}\".{Describe()}");
                }

                changed = _changed.Task;
            }

            try
            {
                await changed.WaitAsync(timer.Token);
            }
            catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                Assert.Fail($"Kiler.exe printed no line that starts with \"{prefix}\" in {timeout}.{Describe()}");
            }
        }
    }

    /// <summary>Waits for the process to end and for the last of its output; fails when the time is up first. Returns the exit code.</summary>
    public async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timer.CancelAfter(timeout);
        try
        {
            await _process.WaitForExitAsync(timer.Token);
            await Task.WhenAll(_outputEnded.Task, _errorsEnded.Task).WaitAsync(timer.Token);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            Assert.Fail($"Kiler.exe did not end in {timeout}.{Describe()}");
        }

        return _process.ExitCode;
    }

    /// <summary>Ends the process at once, as Task Manager does: the instance file stays, and the mutex is abandoned.</summary>
    public async Task KillAsync()
    {
        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The output and the errors so far, for a test message, with the token hidden.</summary>
    public string Describe()
    {
        lock (_lock)
        {
            var text = new StringBuilder();
            text.AppendLine().AppendLine("Output:");
            _output.ForEach(line => text.AppendLine(line));
            text.AppendLine("Errors:");
            _errors.ForEach(line => text.AppendLine(line));
            return KilerExe.MaskToken(text.ToString());
        }
    }

    /// <summary>Ends the process when it still runs. A test disposes the run, and so does <see cref="KilerExe"/>.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                // When it does not end, the delete of the folder fails and says so.
                _process.WaitForExit(TimeSpan.FromSeconds(10));
            }
        }
        // The process ended by itself between the check and the kill. An exception here would hide the failure of the test.
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException)
        {
        }
        finally
        {
            _process.Dispose();
        }
    }

    private void Add(List<string> lines, TaskCompletionSource ended, string? line)
    {
        TaskCompletionSource changed;
        lock (_lock)
        {
            if (line is null)
            {
                ended.TrySetResult();
            }
            else
            {
                lines.Add(line);
            }

            changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
    }
}

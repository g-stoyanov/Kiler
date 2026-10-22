using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Kiler.App.Tests;

/// <summary>
/// The decision about the local copy and its steps, with fake files, shortcuts and processes. The share file is real, in a
/// temporary folder. The real copy and shortcut have tests of their own; the real start from a shared folder is checked by hand.
/// </summary>
public sealed class LocalCopyTests : IDisposable
{
    private const string Source = @"\\server\share\Kiler\Kiler.exe";

    private readonly TempFolder _folder = new();
    private readonly CopyPlace _place;
    private readonly ShareFile _share;
    private readonly FakeFiles _files = new();
    private readonly FakeShortcuts _shortcuts = new();
    private readonly FakeStarter _starter = new();
    private readonly StringWriter _output = new();
    private readonly StringWriter _errors = new();

    public LocalCopyTests()
    {
        _place = new CopyPlace(Path.Combine(_folder.Path, "Programs", "Kiler", "Kiler.exe"), Path.Combine(_folder.Path, "Start Menu", "Kiler.lnk"));
        _share = new ShareFile(Path.Combine(_folder.Path, "Kiler", "share.json"));
    }

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Decide_StartedFromALocalFolder_RunsHere()
    {
        _files.Network = false;

        Assert.IsType<LocalCopyDecision.RunHere>(LocalCopy().Decide(@"D:\Tools\Kiler.exe"));
    }

    [Fact]
    public void Decide_StartedFromANetworkFolderWithoutACopy_CopiesAndStarts()
    {
        Assert.Equal(new LocalCopyDecision.CopyAndStart(Source), LocalCopy().Decide(Source));
    }

    [Fact]
    public void Decide_StartedFromANetworkFolderWithACopy_StartsTheCopy()
    {
        _files.Existing.Add(_place.ExePath);

        Assert.Equal(new LocalCopyDecision.StartCopy(Source), LocalCopy().Decide(Source));
    }

    [Fact]
    public void Decide_TheCopyItselfOnAProfileThatIsOnANetwork_RunsHere()
    {
        // Otherwise the copy would start itself again and again.
        _files.Existing.Add(_place.ExePath);

        Assert.IsType<LocalCopyDecision.RunHere>(LocalCopy().Decide(_place.ExePath.ToUpperInvariant()));
        Assert.IsType<LocalCopyDecision.RunHere>(LocalCopy().Decide(Path.Combine(_place.Folder, "Kiler (2).exe")));
    }

    [Fact]
    public void Decide_WithoutThePathOfTheExe_RunsHere()
    {
        Assert.IsType<LocalCopyDecision.RunHere>(LocalCopy().Decide(null));
    }

    [Fact]
    public void Start_NoCopyYet_CopiesRemembersTheSharedFolderMakesTheShortcutAndThenStartsTheCopy()
    {
        _starter.OnStart = () =>
        {
            // The copy starts last, after everything it needs is in place.
            Assert.Equal([(Source, _place.ExePath)], _files.Copied);
            Assert.Equal(@"\\server\share\Kiler", _share.TryRead());
            Assert.Equal([(_place.ShortcutPath, _place.ExePath)], _shortcuts.Created);
        };

        var exitCode = LocalCopy().Start(new LocalCopyDecision.CopyAndStart(Source), new StartOptions { NoBrowser = true, Port = 5050 }, _output, _errors);

        Assert.Equal(0, exitCode);
        var (program, arguments) = Assert.Single(_starter.Started);
        Assert.Equal(_place.ExePath, program);
        Assert.Equal(["--no-browser", "--port", "5050"], arguments);
        Assert.Equal(
            Lines(
                $"Kiler copies itself to {_place.Folder}.",
                $"Kiler starts from {_place.Folder}.",
                "From now on, start Kiler from the Start menu, not from the shared folder."),
            _output.ToString());
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void Start_ACopyExists_StartsItAsItIs()
    {
        _files.Existing.Add(_place.ExePath);

        var exitCode = LocalCopy().Start(new LocalCopyDecision.StartCopy(Source), new StartOptions(), _output, _errors);

        Assert.Equal(0, exitCode);
        Assert.Empty(_files.Copied);
        var (program, arguments) = Assert.Single(_starter.Started);
        Assert.Equal(_place.ExePath, program);
        Assert.Empty(arguments);
        Assert.DoesNotContain("copies", _output.ToString(), StringComparison.Ordinal);
        Assert.Contains("From now on, start Kiler from the Start menu", _output.ToString(), StringComparison.Ordinal);
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void Start_FromAnotherSharedFolder_RemembersThatOneAndSaysSo()
    {
        _files.Existing.Add(_place.ExePath);
        _share.Write(@"\\old-server\share\Kiler");

        LocalCopy().Start(new LocalCopyDecision.StartCopy(@"K:\Kiler\Kiler.exe"), new StartOptions(), _output, _errors);

        // A mapped drive stays a drive.
        Assert.Equal(@"K:\Kiler", _share.TryRead());
        Assert.Contains(@"The shared folder changes from \\old-server\share\Kiler to K:\Kiler.", _output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"\\server\share\Kiler")]
    [InlineData(@"\\SERVER\Share\Kiler")]
    public void Start_FromTheSameSharedFolder_SaysNothingAboutIt(string before)
    {
        _files.Existing.Add(_place.ExePath);
        _share.Write(before);

        LocalCopy().Start(new LocalCopyDecision.StartCopy(Source), new StartOptions(), _output, _errors);

        Assert.DoesNotContain("shared folder changes", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Start_TheShortcutExists_LeavesItAsItIs()
    {
        _files.Existing.Add(_place.ExePath);
        _files.Existing.Add(_place.ShortcutPath);

        LocalCopy().Start(new LocalCopyDecision.StartCopy(Source), new StartOptions(), _output, _errors);

        Assert.Empty(_shortcuts.Created);
        Assert.Single(_starter.Started);
    }

    [Fact]
    public void Start_AnotherStartMadeTheCopyFirst_StartsThatCopy()
    {
        _files.TargetAppears = true;

        var exitCode = LocalCopy().Start(new LocalCopyDecision.CopyAndStart(Source), new StartOptions(), _output, _errors);

        Assert.Equal(0, exitCode);
        Assert.Single(_starter.Started);
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void Start_TheCopyFails_EndsWithOneAndStartsNothing()
    {
        _files.CopyError = new IOException("There is not enough space on the disk.");

        var exitCode = LocalCopy().Start(new LocalCopyDecision.CopyAndStart(Source), new StartOptions(), _output, _errors);

        Assert.Equal(1, exitCode);
        Assert.Empty(_starter.Started);
        Assert.Contains(_place.Folder, _errors.ToString(), StringComparison.Ordinal);
        Assert.Null(_share.TryRead());
    }

    [Fact]
    public void Start_TheSharedFolderCannotBeRemembered_EndsWithOneAndStartsNothing()
    {
        // A folder where the file should be.
        Directory.CreateDirectory(_share.Path);
        _files.Existing.Add(_place.ExePath);

        var exitCode = LocalCopy().Start(new LocalCopyDecision.StartCopy(Source), new StartOptions(), _output, _errors);

        Assert.Equal(1, exitCode);
        Assert.Empty(_starter.Started);
        Assert.Contains(_share.Path, _errors.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ShortcutErrors))]
    public void Start_TheShortcutFails_SaysSoAndStillStartsTheCopyFromItsPath(Exception error)
    {
        _files.Existing.Add(_place.ExePath);
        _shortcuts.Error = error;

        var exitCode = LocalCopy().Start(new LocalCopyDecision.StartCopy(Source), new StartOptions(), _output, _errors);

        Assert.Equal(0, exitCode);
        Assert.Single(_starter.Started);
        Assert.Contains("shortcut", _errors.ToString(), StringComparison.Ordinal);
        Assert.Contains($"From now on, start {_place.ExePath}, not the Kiler.exe in the shared folder.", _output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A failed HRESULT becomes one of several exception types, E_INVALIDARG an ArgumentException among them.</summary>
    public static TheoryData<Exception> ShortcutErrors() =>
        [new COMException("The shell refused."), new ArgumentException("The parameter is incorrect."), new InvalidCastException("No such interface.")];

    [Fact]
    public void Start_TheCopyDoesNotStart_EndsWithOne()
    {
        _files.Existing.Add(_place.ExePath);
        _starter.Error = new Win32Exception(5, "Access is denied.");

        var exitCode = LocalCopy().Start(new LocalCopyDecision.StartCopy(Source), new StartOptions(), _output, _errors);

        Assert.Equal(1, exitCode);
        Assert.Contains(_place.ExePath, _errors.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("starts from", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Start_WhenKilerRunsHere_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => LocalCopy().Start(new LocalCopyDecision.RunHere(), new StartOptions(), _output, _errors));
        Assert.Empty(_starter.Started);
    }

    private static string Lines(params string[] lines) => string.Concat(lines.Select(line => line + Environment.NewLine));

    private LocalCopy LocalCopy() => new(_place, _share, _files, _shortcuts, _starter);

    private sealed class FakeFiles : ICopyFiles
    {
        public bool Network { get; set; } = true;

        public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(string Source, string Target)> Copied { get; } = [];

        public Exception? CopyError { get; set; }

        /// <summary>Another start makes the copy just before this one renames its own.</summary>
        public bool TargetAppears { get; set; }

        public bool IsOnNetwork(string path) => Network;

        public bool Exists(string path) => Existing.Contains(path);

        public bool CopyNew(string source, string target)
        {
            if (CopyError is not null)
            {
                throw CopyError;
            }

            if (TargetAppears || !Existing.Add(target))
            {
                return false;
            }

            Copied.Add((source, target));
            return true;
        }
    }

    private sealed class FakeShortcuts : IShortcuts
    {
        public List<(string Path, string Target)> Created { get; } = [];

        public Exception? Error { get; set; }

        public void Create(string path, string target)
        {
            if (Error is not null)
            {
                throw Error;
            }

            Created.Add((path, target));
        }
    }

    private sealed class FakeStarter : IProcessStarter
    {
        public List<(string Program, string[] Arguments)> Started { get; } = [];

        public Exception? Error { get; set; }

        public Action? OnStart { get; set; }

        public void Start(string program, IReadOnlyList<string> arguments)
        {
            if (Error is not null)
            {
                throw Error;
            }

            OnStart?.Invoke();
            Started.Add((program, [.. arguments]));
        }
    }
}

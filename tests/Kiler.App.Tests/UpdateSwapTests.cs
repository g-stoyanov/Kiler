using System.Text;
using Kiler.Crypto;

namespace Kiler.App.Tests;

/// <summary>
/// The swap of a checked new version into the place of the local copy (M3.7), with the renames and the deletes behind a fake
/// that logs each step in order, and three tests on the real files in a temporary folder.
/// </summary>
public sealed class UpdateSwapTests : IDisposable
{
    private const string NewName = "Kiler.exe.0123456789abcdef0123456789abcdef.new";

    private const string InPlaceLine = "Kiler 1.0.1 is in place. It runs from the next start of Kiler.";

    private static readonly ReleaseVersion Next = new(1, 0, 1);

    private readonly TempFolder _folder = new();
    private readonly CopyPlace _place;
    private readonly FakeFiles _files;
    private readonly StringWriter _output = new();
    private readonly StringWriter _errors = new();
    private readonly CancellationTokenSource _stop = new();

    public UpdateSwapTests()
    {
        _place = new CopyPlace(Path.Combine(_folder.Path, "Programs", "Kiler", "Kiler.exe"), Path.Combine(_folder.Path, "Start Menu", "Kiler.lnk"));
        _files = new FakeFiles(_place.Folder);
        _files.Existing.Add(_place.ExePath);
    }

    private string NewPath => Path.Combine(_place.Folder, NewName);

    private string OldPath => _place.ExePath + ".old";

    /// <summary>The names of the files in the fake folder of the copy, in order.</summary>
    private string[] Existing => [.. _files.Existing.Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    public void Dispose()
    {
        _stop.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public void Run_RenamesTheRunningExeToOldFirst_ThenTheNewFileByItsNameWhileItIsHeld_AndLetsItGoLast()
    {
        Assert.True(Swap().Run(Update()));

        Assert.Equal(["rename Kiler.exe Kiler.exe.old", $"rename {NewName} Kiler.exe held", "let go"], _files.Log);
        Assert.Equal(["Kiler.exe", "Kiler.exe.old"], Existing);
        Assert.Equal(Lines(InPlaceLine), _output.ToString());
        Assert.Empty(_errors.ToString());
    }

    [Theory]
    [InlineData(unchecked((int)0x800700B7))]
    [InlineData(unchecked((int)0x80070050))]
    public void Run_AnOldExeIsThere_RenamesNothing_DeletesTheNewFile_AndSaysItGoesInLater(int hresult)
    {
        // Another Kiler of the user has put a new version in place already, or still runs from the .old.
        _files.Existing.Add(OldPath);
        _files.RenameErrors.Enqueue(new IOException("Cannot create a file when that file already exists.", hresult));

        Assert.False(Swap().Run(Update()));

        Assert.Equal(["rename Kiler.exe Kiler.exe.old", $"delete {NewName} held", "let go"], _files.Log);
        Assert.Equal(["Kiler.exe", "Kiler.exe.old"], Existing);
        Assert.Equal(Lines($"Kiler 1.0.1 goes into place at a later start, as {OldPath} is still there: a Kiler may still run from it."), _output.ToString());
        Assert.Empty(_errors.ToString());
    }

    public static TheoryData<Exception> RenameErrors => new()
    {
        new IOException("The process cannot access the file because it is being used by another process."),
        new UnauthorizedAccessException("Access to the path is denied."),
        new FileNotFoundException("Could not find file."),
    };

    [Theory]
    [MemberData(nameof(RenameErrors))]
    public void Run_TheRunningExeCannotBeRenamed_DeletesTheNewFileAndSaysSo(Exception error)
    {
        _files.RenameErrors.Enqueue(error);

        Assert.False(Swap().Run(Update()));

        Assert.Equal(["rename Kiler.exe Kiler.exe.old", $"delete {NewName} held", "let go"], _files.Log);
        Assert.Equal(["Kiler.exe"], Existing);
        Assert.Empty(_output.ToString());
        Assert.Equal(Lines($"Kiler cannot put Kiler 1.0.1 in place now, so it goes on with its version: {error.Message}"), _errors.ToString());
    }

    [Fact]
    public void Run_TheNewFileCannotBeRenamed_MovesTheOldExeBack_DeletesTheNewFile_AndSaysSo()
    {
        var error = new IOException("The process cannot access the file because it is being used by another process.");
        _files.RenameErrors.Enqueue(null);
        _files.RenameErrors.Enqueue(error);

        Assert.False(Swap().Run(Update()));

        Assert.Equal(
            ["rename Kiler.exe Kiler.exe.old", $"rename {NewName} Kiler.exe held", "rename Kiler.exe.old Kiler.exe", $"delete {NewName} held", "let go"],
            _files.Log);
        Assert.Equal(["Kiler.exe"], Existing);
        Assert.Empty(_output.ToString());
        Assert.Equal(Lines($"Kiler cannot put Kiler 1.0.1 in place, so it moves its own exe back and goes on with its version: {error.Message}"), _errors.ToString());
    }

    [Fact]
    public void Run_TheOldExeCannotBeMovedBackInFourTries_SaysSoAndHowToGetKilerBack()
    {
        var error = new IOException("The process cannot access the file because it is being used by another process.");
        var back = new UnauthorizedAccessException("Access to the path is denied.");
        _files.RenameErrors.Enqueue(null);
        _files.RenameErrors.Enqueue(error);
        for (var tries = 0; tries < 4; tries++)
        {
            _files.RenameErrors.Enqueue(back);
        }

        Assert.False(Swap().Run(Update()));

        Assert.Equal(4, _files.Log.Count(step => step == "rename Kiler.exe.old Kiler.exe"));
        Assert.Equal(["Kiler.exe.old"], Existing);
        Assert.Equal(
            Lines($"Kiler cannot put Kiler 1.0.1 in place: {error.Message} Nor can it move its own exe back from {OldPath} to {_place.ExePath}: {back.Message} If there is no Kiler.exe there, rename it back by hand: until then Kiler does not start from the Start menu."),
            _errors.ToString());
    }

    [Fact]
    public void Run_TheOldExeMovesBackAtALaterTry_AsTheAntivirusLetsItGo()
    {
        var error = new IOException("The process cannot access the file because it is being used by another process.");
        _files.RenameErrors.Enqueue(null);
        _files.RenameErrors.Enqueue(error);
        _files.RenameErrors.Enqueue(new IOException("The process cannot access the file because it is being used by another process."));

        Assert.False(Swap().Run(Update()));

        Assert.Equal(2, _files.Log.Count(step => step == "rename Kiler.exe.old Kiler.exe"));
        Assert.Equal(["Kiler.exe"], Existing);
        Assert.Equal(Lines($"Kiler cannot put Kiler 1.0.1 in place, so it moves its own exe back and goes on with its version: {error.Message}"), _errors.ToString());
    }

    [Fact]
    public void Run_TheNameOfTheExeIsTakenWhenItMovesBack_DoesNotTryAgain()
    {
        _files.RenameErrors.Enqueue(null);
        _files.RenameErrors.Enqueue(new IOException("Cannot create a file when that file already exists.", unchecked((int)0x800700B7)));
        _files.RenameErrors.Enqueue(new IOException("Cannot create a file when that file already exists.", unchecked((int)0x800700B7)));

        Assert.False(Swap().Run(Update()));

        Assert.Equal(1, _files.Log.Count(step => step == "rename Kiler.exe.old Kiler.exe"));
    }

    [Fact]
    public void Run_AFaultOfTheCodeAtTheSecondRename_MovesTheOldExeBackAndSaysIt()
    {
        _files.RenameErrors.Enqueue(null);
        _files.RenameErrors.Enqueue(new InvalidOperationException("A fault of the code."));

        Assert.False(Swap().Run(Update()));

        Assert.Equal(["Kiler.exe"], Existing);
        Assert.StartsWith(
            "Kiler cannot put Kiler 1.0.1 in place, so it moves its own exe back and goes on with its version: System.InvalidOperationException: A fault of the code.",
            _errors.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Run_AFaultOfTheCodeAtTheFirstRename_DeletesTheNewFileAndSaysIt()
    {
        _files.RenameErrors.Enqueue(new InvalidOperationException("A fault of the code."));

        Assert.False(Swap().Run(Update()));

        Assert.Equal(["Kiler.exe"], Existing);
        Assert.StartsWith(
            "Kiler cannot put Kiler 1.0.1 in place now, so it goes on with its version: System.InvalidOperationException: A fault of the code.",
            _errors.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Run_TheDeleteOfTheNewFileFailsWhileItIsHeld_TriesAgainOnceItIsLetGo()
    {
        _files.Existing.Add(OldPath);
        _files.DeleteFails.Enqueue(true);

        Assert.False(Swap().Run(Update()));

        Assert.Equal(["rename Kiler.exe Kiler.exe.old", $"delete {NewName} held", "let go", $"delete {NewName}"], _files.Log);
        Assert.Equal(["Kiler.exe", "Kiler.exe.old"], Existing);
    }

    [Fact]
    public void Run_KilerIsStopping_RenamesNothing_DeletesTheNewFile_AndSaysNothing()
    {
        _stop.Cancel();

        Assert.False(Swap().Run(Update()));

        Assert.Equal([$"delete {NewName} held", "let go"], _files.Log);
        Assert.Equal(["Kiler.exe"], Existing);
        Assert.Empty(_output.ToString());
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void Run_AfterClose_RenamesNothing_DeletesTheNewFile_AndSaysNothing()
    {
        var swap = Swap();
        swap.Close();

        Assert.False(swap.Run(Update()));

        Assert.Equal([$"delete {NewName} held", "let go"], _files.Log);
        Assert.Equal(["Kiler.exe"], Existing);
        Assert.Empty(_output.ToString());
    }

    [Fact]
    public async Task Close_WaitsForASwapUnderWay_WhichEndsInPlace()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _files.OnRename = () =>
        {
            _files.OnRename = null;
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };
        var swap = Swap();
        var update = Update();

        var running = Task.Run(() => swap.Run(update), TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), "The swap did not start.");
        var closing = Task.Run(swap.Close, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        // A slow machine can only make a broken Close look as if it waited, never the other way.
        Assert.False(closing.IsCompleted, "Close did not wait for the swap under way.");
        release.Set();
        await closing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(await running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal(["Kiler.exe", "Kiler.exe.old"], Existing);
    }

    [Fact]
    public void Run_TheConsoleHasGone_StillPutsTheNewVersionInPlace()
    {
        using var gone = new GoneWriter();

        Assert.True(new UpdateSwap(_place, _files, gone, gone, _stop.Token, TimeSpan.Zero).Run(Update()));

        Assert.Equal(["Kiler.exe", "Kiler.exe.old"], Existing);
    }

    [Fact]
    public void Run_OnTheRealFiles_PutsTheNewVersionInPlace_KeepsTheOldExe_AndLetsTheNewOneGo()
    {
        var files = RealFolder();

        Assert.True(new UpdateSwap(_place, files, _output, _errors, _stop.Token).Run(RealUpdate(files)));

        Assert.Equal([4, 5, 6], File.ReadAllBytes(_place.ExePath));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(OldPath));
        Assert.Equal(2, Directory.GetFiles(_place.Folder).Length);
        Assert.Equal(Lines(InPlaceLine), _output.ToString());
        Assert.Empty(_errors.ToString());

        // Let go: a start can open the new exe.
        CopyFilesTests.OpenToWrite(_place.ExePath).Dispose();
    }

    [Fact]
    public void Run_OnTheRealFilesWithAnOldExe_ChangesNothingAndLeavesNoNewFile()
    {
        var files = RealFolder();
        File.WriteAllBytes(OldPath, [9]);

        Assert.False(new UpdateSwap(_place, files, _output, _errors, _stop.Token).Run(RealUpdate(files)));

        Assert.Equal([1, 2, 3], File.ReadAllBytes(_place.ExePath));
        Assert.Equal([9], File.ReadAllBytes(OldPath));
        Assert.Equal(2, Directory.GetFiles(_place.Folder).Length);
        Assert.Equal(Lines($"Kiler 1.0.1 goes into place at a later start, as {OldPath} is still there: a Kiler may still run from it."), _output.ToString());
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void Run_OnTheRealFiles_TheNewFileHeldWithoutSharingTheDelete_MovesTheOldExeBack()
    {
        var files = RealFolder();
        var update = RealUpdate(files);

        // As an antivirus may hold a new exe while it reads it: the rename of the .new needs the delete that it does not share.
        using (new FileStream(NewPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.False(new UpdateSwap(_place, files, _output, _errors, _stop.Token).Run(update));

            Assert.Equal([1, 2, 3], File.ReadAllBytes(_place.ExePath));
            Assert.False(File.Exists(OldPath), "The old exe was not moved back.");
            Assert.StartsWith("Kiler cannot put Kiler 1.0.1 in place, so it moves its own exe back and goes on with its version: ", _errors.ToString(), StringComparison.Ordinal);
        }
    }

    private static string Lines(params string[] lines) => string.Concat(lines.Select(line => line + Environment.NewLine));

    private UpdateSwap Swap() => new(_place, _files, _output, _errors, _stop.Token, TimeSpan.Zero);

    /// <summary>A checked update in the fake folder, held by a stream that logs when it is let go.</summary>
    private CheckedUpdate Update()
    {
        _files.Existing.Add(NewPath);
        _files.Held = new HeldFile(_files.Log);
        return new CheckedUpdate(Next, NewPath, _files.Held);
    }

    /// <summary>The real folder of the copy, with a <c>Kiler.exe</c> of three bytes.</summary>
    private CopyFiles RealFolder()
    {
        Directory.CreateDirectory(_place.Folder);
        File.WriteAllBytes(_place.ExePath, [1, 2, 3]);
        return new CopyFiles();
    }

    /// <summary>A real <c>.new</c> of three other bytes, held as the check holds it.</summary>
    private CheckedUpdate RealUpdate(CopyFiles files)
    {
        var stream = files.CreateUpdate(NewPath);
        stream.Write([4, 5, 6]);
        stream.Flush();
        return new CheckedUpdate(Next, NewPath, stream);
    }

    /// <summary>The stream of the fake <c>.new</c>; its dispose is a step of the log.</summary>
    private sealed class HeldFile(List<string> log) : MemoryStream
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (!Disposed)
            {
                Disposed = true;
                log.Add("let go");
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// The folder of the copy in memory. Each step goes into <see cref="Log"/> by the file names, with <c>held</c> when the
    /// <c>.new</c> was still held then. A rename to a taken name fails as the real one does.
    /// </summary>
    private sealed class FakeFiles(string folder) : IUpdateFiles
    {
        public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Log { get; } = [];

        public HeldFile? Held { get; set; }

        /// <summary>The outcome of each rename, in order: null succeeds, an exception is thrown; the rest succeed.</summary>
        public Queue<Exception?> RenameErrors { get; } = new();

        /// <summary>The deletes that fail, in order; the rest succeed.</summary>
        public Queue<bool> DeleteFails { get; } = new();

        /// <summary>Done at the start of a rename.</summary>
        public Action? OnRename { get; set; }

        public Stream OpenShared(string path) => throw new NotSupportedException();

        public Stream CreateUpdate(string path) => throw new NotSupportedException();

        /// <summary>The check writes the <c>.new</c> to the disk before it gives it (<see cref="UpdateCheckTests"/>), never the swap.</summary>
        public void FlushToDisk(Stream update) => throw new NotSupportedException();

        public void Rename(string from, string to)
        {
            OnRename?.Invoke();
            Log.Add($"rename {Name(from)} {Name(to)}{HeldMark(from)}");
            if (RenameErrors.TryDequeue(out var error) && error is not null)
            {
                throw error;
            }

            if (!Existing.Contains(from))
            {
                throw new FileNotFoundException("No such file in the fake folder.", from);
            }

            if (Existing.Contains(to))
            {
                throw new IOException("Cannot create a file when that file already exists.", unchecked((int)0x800700B7));
            }

            Existing.Remove(from);
            Existing.Add(to);
        }

        public bool TryDelete(string path)
        {
            Log.Add($"delete {Name(path)}{HeldMark(path)}");
            if (DeleteFails.TryDequeue(out var fails) && fails)
            {
                return false;
            }

            Existing.Remove(path);
            return true;
        }

        private string Name(string path)
        {
            Assert.Equal(folder, Path.GetDirectoryName(path));
            return Path.GetFileName(path);
        }

        private string HeldMark(string path) =>
            Path.GetFileName(path) == NewName && Held is { Disposed: false } ? " held" : string.Empty;
    }

    /// <summary>A console that has gone: every line throws.</summary>
    private sealed class GoneWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value) => throw new IOException("The pipe has been ended.");
    }
}

using System.Diagnostics;

namespace Kiler.App.Tests;

/// <summary>
/// What the updates leave in the folder of the copy (M3.7a): on the real files in a temporary folder, with a real process
/// that runs from a <c>.old</c>, and behind a fake that logs each step for the tries, the stop and the faults.
/// </summary>
public sealed class UpdateLeftoversTests : IDisposable
{
    private const string FirstNew = "Kiler.exe.0123456789abcdef0123456789abcdef.new";
    private const string SecondNew = "Kiler.exe.fedcba9876543210fedcba9876543210.new";
    private const string Tmp = "Kiler.exe.00112233445566778899aabbccddeeff.tmp";

    private const string HeldMessage = "The process cannot access the file because it is being used by another process.";

    private readonly TempFolder _folder = new();
    private readonly CopyPlace _place;
    private readonly FakeFiles _fake = new();
    private readonly StringWriter _output = new();
    private readonly StringWriter _errors = new();
    private readonly CancellationTokenSource _stop = new();

    public UpdateLeftoversTests()
    {
        _place = new CopyPlace(Path.Combine(_folder.Path, "Programs", "Kiler", "Kiler.exe"), Path.Combine(_folder.Path, "Start Menu", "Kiler.lnk"));
        Directory.CreateDirectory(_place.Folder);
        File.WriteAllBytes(_place.ExePath, [1, 2, 3]);
    }

    private string OldPath => _place.ExePath + ".old";

    /// <summary>The names of the files in the folder of the copy, in order.</summary>
    private string[] Names => [.. Directory.GetFiles(_place.Folder).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    public void Dispose()
    {
        _stop.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public void Clean_DeletesTheOldExe_TheNewFiles_AndAnOldTmp_AndLeavesEveryOtherFile()
    {
        string[] others =
        [
            "Kiler.exe.new",
            "Kiler.exe.0123456789abcdef0123456789abcdeg.new",
            "Kiler.exe.0123456789abcdef0123456789abcdef0.new",
            "Kiler.exe.0123456789abcdef0123456789abcdef.new.txt",
            "Kiler.exe.0123456789abcdef0123456789abcde.tmp",
            "Kiler.lnk.old",
            "notes.tmp",
        ];
        foreach (var name in others.Append(FirstNew).Append(SecondNew))
        {
            File.WriteAllBytes(Path.Combine(_place.Folder, name), [9]);
        }

        File.WriteAllBytes(OldPath, [4]);
        Old(Path.Combine(_place.Folder, Tmp));
        Directory.CreateDirectory(Path.Combine(_place.Folder, "Kiler.exe.ffeeddccbbaa99887766554433221100.new"));

        Leftovers().Clean();

        Assert.Equal([.. others.Append("Kiler.exe").Order(StringComparer.Ordinal)], Names);
        Assert.True(Directory.Exists(Path.Combine(_place.Folder, "Kiler.exe.ffeeddccbbaa99887766554433221100.new")), "A folder was deleted.");
        Assert.Equal([1, 2, 3], File.ReadAllBytes(_place.ExePath));
        Assert.Empty(_output.ToString());
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void Clean_AReadOnlyOldExeAndTmp_Go()
    {
        // A first copy of an exe that is read-only on the shared folder was read-only before M3.7a, as File.Copy keeps the
        // attribute, and its .old would hold up every later update (security review of M3.7a).
        var tmp = Path.Combine(_place.Folder, Tmp);
        Old(tmp);
        File.WriteAllBytes(OldPath, [4]);
        File.SetAttributes(tmp, FileAttributes.ReadOnly);
        File.SetAttributes(OldPath, FileAttributes.ReadOnly);

        Leftovers().Clean();

        Assert.Equal(["Kiler.exe"], Names);
        Assert.Empty(_output.ToString());
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void Clean_ANewFileThatACheckHolds_StaysAndSaysSo_AndTheOnesThatNoKilerHolds_Go()
    {
        var held = Path.Combine(_place.Folder, "Kiler.exe.11111111111111111111111111111111.new");
        File.WriteAllBytes(Path.Combine(_place.Folder, FirstNew), [9]);
        File.WriteAllBytes(Path.Combine(_place.Folder, SecondNew), [9]);

        // As a check holds its .new until it is in place (M3.6).
        using (var check = new CopyFiles().CreateUpdate(held))
        {
            check.Write([5]);

            Leftovers().Clean();
        }

        Assert.Equal(["Kiler.exe", Path.GetFileName(held)], Names);
        Assert.StartsWith($"Kiler leaves {held} for a later start, as it cannot delete it now: ", _output.ToString(), StringComparison.Ordinal);
        Assert.Single(_output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Empty(_errors.ToString());
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 120, false)]
    [InlineData(120, 0, false)]
    [InlineData(59, 120, false)]
    [InlineData(-10, 120, false)]
    [InlineData(61, 61, true)]
    [InlineData(120, 120, true)]
    public void Clean_ATmpGoesOnlyWhenBothItsTimesAreAnHourOld(int createdMinutesAgo, int writtenMinutesAgo, bool goes)
    {
        // A parallel first copy may be writing it; File.Copy keeps the write time of its source, so the later time counts,
        // and a time ahead of the clock counts as young.
        var tmp = Path.Combine(_place.Folder, Tmp);
        File.WriteAllBytes(tmp, [9]);
        var now = DateTime.UtcNow;
        File.SetCreationTimeUtc(tmp, now.AddMinutes(-createdMinutesAgo));
        File.SetLastWriteTimeUtc(tmp, now.AddMinutes(-writtenMinutesAgo));

        Leftovers().Clean();

        Assert.Equal(!goes, File.Exists(tmp));
        Assert.Empty(_output.ToString());
    }

    [Fact]
    public void Clean_AnOldTmpThatAFirstCopyHolds_StaysAndSaysSo()
    {
        var tmp = Path.Combine(_place.Folder, Tmp);
        Old(tmp);

        // A handle that shares everything still holds it: only a file that no one holds goes.
        using (new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            Leftovers().Clean();
        }

        Assert.True(File.Exists(tmp), "A .tmp that a first copy holds was deleted.");
        Assert.StartsWith($"Kiler leaves {tmp} for a later start, as it cannot delete it now: ", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_AnOldExeThatARunningProcessHolds_StaysAndSaysSo_AndGoesOnceTheProcessHasEnded()
    {
        // The guard rests on Windows refusing to delete an exe that a process runs, not on the share mode of a handle, as
        // the loader opens the exe with FILE_SHARE_DELETE (M3.7, security review): so a real process runs from the .old.
        // A copy of cmd.exe reads its commands from the input, which stays open, so it runs until it is ended.
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), OldPath);
        var start = new ProcessStartInfo(OldPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using (var process = Process.Start(start)!)
        {
            try
            {
                Leftovers().Clean();

                Assert.False(process.HasExited, "The process from the .old ended before the leftovers were deleted.");
            }
            finally
            {
                process.Kill();
                process.WaitForExit(TimeSpan.FromSeconds(30));
            }
        }

        Assert.StartsWith($"Kiler leaves {OldPath} for a later start, as it cannot delete it now: ", _output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Kiler.exe.old", Names);

        // Windows may let the image go a moment after the end of the process: the tries wait for it.
        new UpdateLeftovers(_place, new CopyFiles(), TimeProvider.System, _output, _errors, _stop.Token, TimeSpan.FromMilliseconds(250)).Clean();

        Assert.Equal(["Kiler.exe"], Names);
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void Clean_AnOldExeWithoutAKilerExe_StaysAsTheOnlyExeOfTheCopy()
    {
        // Another Kiler of the user is between the two renames of its swap, or it was cut off there.
        File.Move(_place.ExePath, OldPath);

        Leftovers().Clean();

        Assert.Equal(["Kiler.exe.old"], Names);
        Assert.Empty(_output.ToString());
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void Clean_ALinkUnderTheNameOfALeftover_IsSkipped_AndItsTargetStays()
    {
        // A delete through a handle follows a link and deletes its target (M3.6, security review).
        var target = Path.Combine(_folder.Path, "target.txt");
        File.WriteAllBytes(target, [7]);
        var newLink = Path.Combine(_place.Folder, FirstNew);
        LinkOrSkip(newLink, target);
        LinkOrSkip(OldPath, target);

        Leftovers().Clean();

        Assert.Equal([7], File.ReadAllBytes(target));
        Assert.Equal(["Kiler.exe", FirstNew, "Kiler.exe.old"], Names);
        Assert.Equal(
            Lines(
                $"Kiler leaves {newLink} as it is: it is a link, not a file that Kiler made.",
                $"Kiler leaves {OldPath} as it is: it is a link, not a file that Kiler made."),
            _errors.ToString());
    }

    [Fact]
    public void Clean_NoFolderOfTheCopy_SaysNothing()
    {
        _folder.Dispose();
        Directory.CreateDirectory(_folder.Path);

        Leftovers().Clean();

        Assert.Empty(_output.ToString());
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void MoveBackOld_AnOldExeWithoutAKilerExe_MovesItBackAndSaysSo()
    {
        File.Move(_place.ExePath, OldPath);

        Leftovers().MoveBackOld();

        Assert.Equal(["Kiler.exe"], Names);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(_place.ExePath));
        Assert.Equal(Lines($"Kiler has moved its copy back from {OldPath} to {_place.ExePath}, as an update had stopped halfway."), _output.ToString());
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void MoveBackOld_AKilerExeIsThere_LeavesBothToTheCopy()
    {
        File.WriteAllBytes(OldPath, [4]);

        Leftovers().MoveBackOld();

        Assert.Equal([1, 2, 3], File.ReadAllBytes(_place.ExePath));
        Assert.Equal([4], File.ReadAllBytes(OldPath));
        Assert.Empty(_output.ToString());
    }

    [Fact]
    public void MoveBackOld_NoCopyYet_SaysNothing()
    {
        // The first start from the shared folder: there is no folder of the copy.
        _folder.Dispose();
        Directory.CreateDirectory(_folder.Path);

        Leftovers().MoveBackOld();

        Assert.Empty(_output.ToString());
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void MoveBackOld_ALinkUnderTheNameOfTheOldExe_IsNotMovedBack()
    {
        var target = Path.Combine(_folder.Path, "target.txt");
        File.WriteAllBytes(target, [7]);
        File.Delete(_place.ExePath);
        LinkOrSkip(OldPath, target);

        Leftovers().MoveBackOld();

        Assert.Equal(["Kiler.exe.old"], Names);
        Assert.Equal(Lines($"Kiler leaves {OldPath} as it is: it is a link, not a file that Kiler made."), _errors.ToString());
    }

    [Fact]
    public void Clean_ADeleteThatFailsAtTheFirstTries_GoesAtALaterOne()
    {
        _fake.Existing.Add(_place.ExePath);
        _fake.Existing.Add(OldPath);
        _fake.Errors.Enqueue(new IOException(HeldMessage));
        _fake.Errors.Enqueue(new UnauthorizedAccessException("Access to the path is denied."));

        Fake().Clean();

        Assert.Equal(["delete Kiler.exe.old", "delete Kiler.exe.old", "delete Kiler.exe.old"], _fake.Log);
        Assert.Empty(_output.ToString());
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void Clean_AFileThatStaysAfterFourTries_SaysSoOnce_AndTheOthersStillGo()
    {
        _fake.Existing.Add(_place.ExePath);
        _fake.Existing.Add(Path.Combine(_place.Folder, FirstNew));
        _fake.Existing.Add(Path.Combine(_place.Folder, SecondNew));
        for (var tries = 0; tries < 4; tries++)
        {
            _fake.Errors.Enqueue(new IOException(HeldMessage));
        }

        Fake().Clean();

        Assert.Equal([.. Enumerable.Repeat($"delete unheld {FirstNew}", 4), $"delete unheld {SecondNew}"], _fake.Log);
        Assert.Equal(Lines($"Kiler leaves {Path.Combine(_place.Folder, FirstNew)} for a later start, as it cannot delete it now: {HeldMessage}"), _output.ToString());
    }

    public static TheoryData<Exception> GoneErrors => new()
    {
        new FileNotFoundException("Could not find file."),
        new DirectoryNotFoundException("Could not find a part of the path."),
    };

    [Theory]
    [MemberData(nameof(GoneErrors))]
    public void Clean_AFileThatGoesBetweenTheTries_IsDeleted(Exception gone)
    {
        // Another start of the user has deleted it, or the folder has gone.
        _fake.Existing.Add(_place.ExePath);
        _fake.Existing.Add(Path.Combine(_place.Folder, FirstNew));
        _fake.Errors.Enqueue(new IOException(HeldMessage));
        _fake.Errors.Enqueue(gone);

        Fake().Clean();

        Assert.Equal(2, _fake.Log.Count);
        Assert.Empty(_output.ToString());
    }

    [Fact]
    public void Clean_TheFolderCannotBeRead_SaysSoAndDoesNotThrow()
    {
        _fake.ListError = new UnauthorizedAccessException("Access to the path is denied.");

        Fake().Clean();

        Assert.Equal(Lines($"Kiler cannot clear what its updates left in {_place.Folder}: Access to the path is denied."), _errors.ToString());
    }

    [Fact]
    public void Clean_AFaultOfTheCode_SaysItWithTheWholeException_AndDoesNotThrow()
    {
        _fake.Existing.Add(_place.ExePath);
        _fake.Existing.Add(OldPath);
        _fake.Errors.Enqueue(new InvalidOperationException("A fault of the code."));

        Fake().Clean();

        Assert.StartsWith($"Kiler cannot clear what its updates left in {_place.Folder}: System.InvalidOperationException: A fault of the code.", _errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_KilerHasStopped_DeletesNothingAndSaysNothing()
    {
        _fake.Existing.Add(_place.ExePath);
        _fake.Existing.Add(OldPath);
        _stop.Cancel();

        Fake().Clean();

        Assert.Empty(_fake.Log);
        Assert.Empty(_output.ToString());
    }

    [Fact]
    public void Clean_KilerStopsDuringTheTries_StopsThereAndSaysNothing()
    {
        _fake.Existing.Add(_place.ExePath);
        _fake.Existing.Add(Path.Combine(_place.Folder, FirstNew));
        _fake.Existing.Add(Path.Combine(_place.Folder, SecondNew));
        _fake.OnDelete = _stop.Cancel;
        _fake.Errors.Enqueue(new IOException(HeldMessage));

        Fake().Clean();

        Assert.Equal([$"delete unheld {FirstNew}"], _fake.Log);
        Assert.Empty(_output.ToString());
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void MoveBackOld_AKilerExeIsThere_TriesNoRename()
    {
        _fake.Existing.Add(_place.ExePath);
        _fake.Existing.Add(OldPath);

        Fake().MoveBackOld();

        Assert.Empty(_fake.Log);
    }

    [Fact]
    public void MoveBackOld_TheRenameFailsFourTimes_SaysSo()
    {
        _fake.Existing.Add(OldPath);
        for (var tries = 0; tries < 4; tries++)
        {
            _fake.Errors.Enqueue(new IOException(HeldMessage));
        }

        Fake().MoveBackOld();

        Assert.Equal(Enumerable.Repeat("rename Kiler.exe.old Kiler.exe", 4), _fake.Log);
        Assert.Empty(_output.ToString());
        Assert.Equal(Lines($"Kiler cannot move its copy back from {OldPath} to {_place.ExePath}: {HeldMessage}"), _errors.ToString());
    }

    [Theory]
    [InlineData(unchecked((int)0x800700B7))]
    [InlineData(unchecked((int)0x80070050))]
    public void MoveBackOld_ANameTakenMeanwhile_IsNotTriedAgain_AndSaysNothing(int hresult)
    {
        // Another start of the user has put an exe there: the copy is there.
        _fake.Existing.Add(OldPath);
        _fake.Errors.Enqueue(new IOException("Cannot create a file when that file already exists.", hresult));

        Fake().MoveBackOld();

        Assert.Equal(["rename Kiler.exe.old Kiler.exe"], _fake.Log);
        Assert.Empty(_output.ToString());
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void MoveBackOld_AFaultOfTheCode_SaysItAndDoesNotThrow()
    {
        _fake.ListError = new InvalidOperationException("A fault of the code.");

        Fake().MoveBackOld();

        Assert.StartsWith($"Kiler cannot look for what its updates left in {_place.Folder}: System.InvalidOperationException: A fault of the code.", _errors.ToString(), StringComparison.Ordinal);
    }

    private static string Lines(params string[] lines) => string.Concat(lines.Select(line => line + Environment.NewLine));

    /// <summary>A file whose two times are two hours old.</summary>
    private static void Old(string path)
    {
        File.WriteAllBytes(path, [9]);
        var then = DateTime.UtcNow.AddHours(-2);
        File.SetCreationTimeUtc(path, then);
        File.SetLastWriteTimeUtc(path, then);
    }

    /// <summary>A symbolic link to a file; the test is skipped where Windows does not let this account make one.</summary>
    private static void LinkOrSkip(string path, string target)
    {
        try
        {
            File.CreateSymbolicLink(path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"This account cannot make a symbolic link: {ex.Message}");
        }
    }

    private UpdateLeftovers Leftovers() => new(_place, new CopyFiles(), TimeProvider.System, _output, _errors, _stop.Token, TimeSpan.Zero);

    private UpdateLeftovers Fake() => new(_place, _fake, TimeProvider.System, _output, _errors, _stop.Token, TimeSpan.Zero);

    /// <summary>
    /// The folder of the copy in memory, whose files are all two hours old. Each delete and rename goes into
    /// <see cref="Log"/> by the file names.
    /// </summary>
    private sealed class FakeFiles : ILeftoverFiles
    {
        public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Log { get; } = [];

        /// <summary>The outcome of each delete and rename, in order: an exception is thrown; the rest succeed.</summary>
        public Queue<Exception> Errors { get; } = new();

        public Exception? ListError { get; set; }

        /// <summary>Done at the start of a delete.</summary>
        public Action? OnDelete { get; set; }

        public IReadOnlyList<FolderFile> List(string folder)
        {
            if (ListError is not null)
            {
                throw ListError;
            }

            var then = DateTimeOffset.UtcNow.AddHours(-2);
            return [.. Existing.Select(path => new FolderFile(path, FileAttributes.Archive, then, then))];
        }

        public bool Exists(string path) => Existing.Contains(path);

        public void Rename(string from, string to)
        {
            Log.Add($"rename {Path.GetFileName(from)} {Path.GetFileName(to)}");
            Fail();
            Existing.Remove(from);
            Existing.Add(to);
        }

        public void Delete(string path)
        {
            OnDelete?.Invoke();
            Log.Add($"delete {Path.GetFileName(path)}");
            Fail();
            Existing.Remove(path);
        }

        public void DeleteUnheld(string path)
        {
            OnDelete?.Invoke();
            Log.Add($"delete unheld {Path.GetFileName(path)}");
            Fail();
            Existing.Remove(path);
        }

        private void Fail()
        {
            if (Errors.TryDequeue(out var error))
            {
                throw error;
            }
        }
    }
}

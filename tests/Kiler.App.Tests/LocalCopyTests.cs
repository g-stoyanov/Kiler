using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Kiler.Core;
using Kiler.Crypto;

namespace Kiler.App.Tests;

/// <summary>
/// The decision about the local copy and its steps, with fake files, shortcuts and processes, and keys made by the tests.
/// The share file is real, in a temporary folder. The real files have tests of their own, and two tests here make the
/// first copy on them; the real start from a shared folder is checked by hand.
/// </summary>
public sealed class LocalCopyTests : IDisposable
{
    private const string ShareFolder = @"\\server\share\Kiler";
    private const string Source = ShareFolder + @"\Kiler.exe";
    private const string ShareVersion = ShareFolder + @"\version.json";

    /// <summary>The version of the running exe in the tests.</summary>
    private static readonly ReleaseVersion Own = new(1, 0, 0);

    private readonly TempFolder _folder = new();
    private readonly CopyPlace _place;
    private readonly ShareFile _share;
    private readonly FakeFiles _files = new();
    private readonly FakeUpdateFiles _updates = new();
    private readonly FakeShortcuts _shortcuts = new();
    private readonly FakeStarter _starter = new();
    private readonly SigningKey _key = SigningKey.New();
    private readonly byte[] _exe = RandomNumberGenerator.GetBytes(100_000);
    private readonly StringWriter _output = new();
    private readonly StringWriter _errors = new();

    public LocalCopyTests()
    {
        _place = new CopyPlace(Path.Combine(_folder.Path, "Programs", "Kiler", "Kiler.exe"), Path.Combine(_folder.Path, "Start Menu", "Kiler.lnk"));
        _share = new ShareFile(Path.Combine(_folder.Path, "Kiler", "share.json"));
    }

    private string OldPath => _place.ExePath + ".old";

    /// <summary>The one temporary file that the first copy made.</summary>
    private string TmpPath => Assert.Single(_updates.Created).Key;

    private string CopyLine => $"Kiler copies itself to {_place.Folder} and checks the copy.";

    public void Dispose()
    {
        _key.Dispose();
        _folder.Dispose();
    }

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
    public void Decide_OnlyTheOldExeOfASwapCutOff_CopiesAndStarts_AndMovesNothing()
    {
        // --diag decides too, and changes nothing.
        _files.Existing.Add(OldPath);

        Assert.Equal(new LocalCopyDecision.CopyAndStart(Source), LocalCopy().Decide(Source));

        Assert.Empty(_files.Renamed);
    }

    [Fact]
    public void DecideStart_StartedFromANetworkFolderWithOnlyTheOldExeOfASwapCutOff_MovesItBackAndStartsIt()
    {
        // The copy that was there takes only a version higher than its own; a new copy would take what the shared folder holds.
        _files.Existing.Add(OldPath);

        Assert.Equal(new LocalCopyDecision.StartCopy(Source), LocalCopy().DecideStart(Source, Leftovers()));

        Assert.Equal([(OldPath, _place.ExePath)], _files.Renamed);
        Assert.Equal(Lines($"Kiler has moved its copy back from {OldPath} to {_place.ExePath}, as an update had stopped halfway."), _output.ToString());
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void DecideStart_StartedFromANetworkFolderWithoutACopyOrAnOldExe_CopiesAndStarts()
    {
        Assert.Equal(new LocalCopyDecision.CopyAndStart(Source), LocalCopy().DecideStart(Source, Leftovers()));

        Assert.Empty(_files.Renamed);
        Assert.Empty(_output.ToString());
    }

    [Fact]
    public void DecideStart_StartedFromANetworkFolderWithACopyAndItsOldExe_LeavesTheOldExeToTheCopy()
    {
        _files.Existing.Add(_place.ExePath);
        _files.Existing.Add(OldPath);

        Assert.Equal(new LocalCopyDecision.StartCopy(Source), LocalCopy().DecideStart(Source, Leftovers()));

        Assert.Empty(_files.Renamed);
        Assert.Empty(_output.ToString());
    }

    [Fact]
    public void DecideStart_StartedFromALocalFolderOrTheCopy_LeavesTheFolderOfTheCopyAsItIs()
    {
        _files.Network = false;
        _files.Existing.Add(OldPath);

        Assert.IsType<LocalCopyDecision.RunHere>(LocalCopy().DecideStart(@"D:\Tools\Kiler.exe", Leftovers()));
        Assert.IsType<LocalCopyDecision.RunHere>(LocalCopy().DecideStart(_place.ExePath, Leftovers()));

        Assert.Empty(_files.Renamed);
    }

    [Fact]
    public void Start_NoCopyYet_CopiesAndChecksRemembersTheSharedFolderMakesTheShortcutAndThenStartsTheCopy()
    {
        Release(Own, _exe, Signature(Own, _exe));
        _starter.OnStart = () =>
        {
            // The copy starts last, after everything it needs is in place, and once it is let go.
            var tmp = TmpPath;
            Assert.Equal([(tmp, _place.ExePath, true)], _updates.Renamed);
            Assert.True(_updates.Created[tmp].FlushedToDisk, "The checked copy is not on the disk before its rename.");
            Assert.True(_updates.Created[tmp].Disposed, "The copy is still held when it starts.");
            Assert.Equal(@"\\server\share\Kiler", _share.TryRead());
            Assert.Equal([(_place.ShortcutPath, _place.ExePath)], _shortcuts.Created);
        };

        var exitCode = LocalCopy().Start(new LocalCopyDecision.CopyAndStart(Source), new StartOptions { NoBrowser = true, Port = 5050 }, _output, _errors);

        Assert.Equal(0, exitCode);
        var (program, arguments) = Assert.Single(_starter.Started);
        Assert.Equal(_place.ExePath, program);
        Assert.Equal(["--no-browser", "--port", "5050"], arguments);
        Assert.True(_updates.Created[TmpPath].ToArray().AsSpan().SequenceEqual(_exe), "The copy does not hold the exe of the shared folder.");
        Assert.Equal([ShareVersion, Source], _updates.Opened);
        Assert.Empty(_updates.Deleted);
        Assert.Equal(
            Lines(
                CopyLine,
                $"Kiler starts from {_place.Folder}.",
                "From now on, start Kiler from the Start menu, not from the shared folder."),
            _output.ToString());
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void Start_NoCopyYet_MakesTheTemporaryFileUnderANameOfItsOwnNextToTheCopy()
    {
        // The leftovers of the updates delete only the names that Kiler gives (M3.7a).
        Release(Own, _exe, Signature(Own, _exe));

        LocalCopy().Start(new LocalCopyDecision.CopyAndStart(Source), new StartOptions(), _output, _errors);

        Assert.Equal(_place.Folder, Path.GetDirectoryName(TmpPath));
        Assert.Matches(@"^Kiler\.exe\.[0-9a-f]{32}\.tmp$", Path.GetFileName(TmpPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Start_TwoSignaturesOneOfThemWithItsKey_TakesTheCopy(int place)
    {
        using var otherKey = SigningKey.New();
        var signatures = new List<string> { Signature(Own, _exe, otherKey) };
        signatures.Insert(place, Signature(Own, _exe));
        Release(Own, _exe, [.. signatures]);

        var exitCode = LocalCopy().Start(new LocalCopyDecision.CopyAndStart(Source), new StartOptions(), _output, _errors);

        Assert.Equal(0, exitCode);
        Assert.Contains(_place.ExePath, _updates.Existing);
        Assert.Single(_starter.Started);
    }

    [Fact]
    public void Start_NoVersionFile_CopiesNothingWritesNothingAndStartsNothing()
    {
        // Whoever can delete version.json would otherwise skip the check (decided by the user in M3.8).
        _updates.Shared[Source] = new SharedFile(_exe);

        AssertRefusedBeforeTheCopy("The shared folder has no version.json, so Kiler cannot check its copy and does not copy itself.");
    }

    public static TheoryData<Exception> ReadErrors => new()
    {
        new IOException("The network path was not found."),
        new UnauthorizedAccessException("Access is denied."),
    };

    [Theory]
    [MemberData(nameof(ReadErrors))]
    public void Start_TheVersionFileCannotBeRead_CopiesNothingAndStartsNothing(Exception error)
    {
        Release(Own, _exe, Signature(Own, _exe));
        _updates.Shared[ShareVersion].OpenError = error;

        AssertRefusedBeforeTheCopy($"Kiler cannot read {ShareVersion}, so it cannot check its copy and does not copy itself: {error.Message}");
    }

    [Fact]
    public void Start_AVersionFileThatDoesNotFit_CopiesNothingAndStartsNothing()
    {
        _updates.Shared[ShareVersion] = new SharedFile("{}"u8.ToArray());
        _updates.Shared[Source] = new SharedFile(_exe);

        AssertRefusedBeforeTheCopy(null);
        Assert.StartsWith($"{ShareVersion} does not fit, so Kiler does not copy itself: ", _errors.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.0.1")]
    [InlineData("0.9.9")]
    public void Start_TheVersionFileOfAnotherVersion_CopiesNothingAndStartsNothing(string sharedVersion)
    {
        // During a release the exe and version.json of the shared folder change one after the other.
        Assert.True(ReleaseVersion.TryParse(sharedVersion, out var version));
        Release(version, _exe, Signature(version, _exe));

        AssertRefusedBeforeTheCopy(
            $"{ShareVersion} is for Kiler {sharedVersion}, not for this Kiler 1.0.0, so Kiler does not copy itself; if a new version is on its way there, start Kiler again in a few minutes.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("1.0.0-dev")]
    public void Start_ItsOwnVersionIsNotMajorMinorPatch_ReadsNothingAndStartsNothing(string own)
    {
        Release(Own, _exe, Signature(Own, _exe));

        var exitCode = LocalCopy(own).Start(new LocalCopyDecision.CopyAndStart(Source), new StartOptions(), _output, _errors);

        Assert.Equal(1, exitCode);
        Assert.Empty(_updates.Opened);
        AssertNothingDone();
        Assert.Equal(Lines($"Kiler cannot check its copy, so it does not copy itself: its own version {own} is not major.minor.patch."), _errors.ToString());
    }

    [Fact]
    public void Start_AnExeLargerThanTheLimitByItsLength_CopiesNothingAndStartsNothing()
    {
        Release(Own, _exe, Signature(Own, _exe));
        _updates.Shared[Source].Length = VersionFile.MaxExeSize + 1L;

        AssertRefusedBeforeTheCopy($"{Source} is larger than {VersionFile.MaxExeSize} bytes, so Kiler does not copy itself.");
    }

    public static TheoryData<string> NotSignedCases => ["NotASignature", "AnotherKey", "AnotherExe", "AnotherVersion"];

    [Theory]
    [MemberData(nameof(NotSignedCases))]
    public void Start_ACopyThatTheVersionFileDoesNotSign_DeletesItWhileItIsHeldAndStartsNothing(string kind)
    {
        using var otherKey = SigningKey.New();
        var signature = kind switch
        {
            "NotASignature" => "not a signature",
            "AnotherKey" => Signature(Own, _exe, otherKey),
            "AnotherExe" => Signature(Own, RandomNumberGenerator.GetBytes(_exe.Length)),
            "AnotherVersion" => Signature(new ReleaseVersion(1, 0, 1), _exe),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        Release(Own, _exe, signature);

        AssertRefusedAfterTheCopy($"{Source} is not the Kiler 1.0.0 that version.json signs, so Kiler does not copy itself.");
    }

    [Fact]
    public void Start_TheCheckedCopyCannotBeWrittenToTheDisk_DeletesItAndStartsNothing()
    {
        Release(Own, _exe, Signature(Own, _exe));
        _updates.FlushError = new IOException("The disk is full.");

        AssertRefusedAfterTheCopy($"Kiler cannot copy itself to {_place.Folder}: The disk is full.");
    }

    [Fact]
    public void Start_AFaultOfTheCodeInTheCheck_DeletesTheCopyAndStartsNothing()
    {
        // Also a DLL that the check needs and that the policy of M3.8 refuses from the shared folder.
        Release(Own, _exe, Signature(Own, _exe));
        _updates.FlushError = new InvalidOperationException("A fault of the code.");

        var exitCode = LocalCopy().Start(new LocalCopyDecision.CopyAndStart(Source), new StartOptions(), _output, _errors);

        Assert.Equal(1, exitCode);
        Assert.Equal([(TmpPath, true)], _updates.Deleted);
        Assert.True(_updates.Created[TmpPath].Disposed, "The copy was not let go.");
        Assert.Empty(_updates.Existing);
        AssertNothingDone();
        Assert.StartsWith("Kiler failed while it checked its copy, so it does not copy itself: System.InvalidOperationException: A fault of the code.", _errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Start_TheCopyCannotBeMade_EndsWithOneAndStartsNothing()
    {
        Release(Own, _exe, Signature(Own, _exe));
        _updates.CreateError = new IOException("There is not enough space on the disk.");

        var exitCode = LocalCopy().Start(new LocalCopyDecision.CopyAndStart(Source), new StartOptions(), _output, _errors);

        Assert.Equal(1, exitCode);
        Assert.Empty(_updates.Deleted);
        AssertNothingDone();
        Assert.Equal(Lines($"Kiler cannot copy itself to {_place.Folder}: There is not enough space on the disk."), _errors.ToString());
    }

    [Fact]
    public void Start_TheCheckedCopyCannotBeRenamed_DeletesItWhileItIsHeldAndStartsNothing()
    {
        Release(Own, _exe, Signature(Own, _exe));
        _updates.RenameError = new UnauthorizedAccessException("Access is denied.");

        AssertRefusedAfterTheCopy($"Kiler cannot copy itself to {_place.Folder}: Access is denied.");
    }

    [Fact]
    public void Start_AnotherStartMadeTheCopyFirst_DeletesItsOwnAndStartsThatCopy()
    {
        Release(Own, _exe, Signature(Own, _exe));
        _updates.Existing.Add(_place.ExePath);

        var exitCode = LocalCopy().Start(new LocalCopyDecision.CopyAndStart(Source), new StartOptions(), _output, _errors);

        Assert.Equal(0, exitCode);
        Assert.Equal([(TmpPath, true)], _updates.Deleted);
        Assert.True(_updates.Created[TmpPath].Disposed, "The copy was not let go.");
        Assert.Equal([_place.ExePath], _updates.Existing);
        Assert.Single(_starter.Started);
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void Start_OnTheRealFiles_PutsTheCheckedCopyInPlace_NotReadOnly_AndLetsItGoBeforeItStarts()
    {
        var source = RealRelease(Signature(Own, _exe));

        // File.Copy kept the attributes of its source until M3.8, and Windows refuses to delete a read-only file, so the .old
        // of a read-only copy would stay for ever and hold up every later update (security review of M3.7a).
        File.SetAttributes(source, FileAttributes.ReadOnly);
        try
        {
            _starter.OnStart = () =>
            {
                // Refused while a handle can still write it.
                using var unshared = new FileStream(_place.ExePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            };

            var exitCode = RealLocalCopy().Start(new LocalCopyDecision.CopyAndStart(source), new StartOptions(), _output, _errors);

            Assert.True(exitCode == 0, _errors.ToString());
            Assert.Single(_starter.Started);
            Assert.True(File.ReadAllBytes(_place.ExePath).AsSpan().SequenceEqual(_exe), "The copy does not hold the exe of the shared folder.");
            Assert.Equal(FileAttributes.None, File.GetAttributes(_place.ExePath) & FileAttributes.ReadOnly);
            Assert.Equal([_place.ExePath], Directory.GetFiles(_place.Folder));
        }
        finally
        {
            File.SetAttributes(source, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Start_OnTheRealFilesWithABadSignature_LeavesNoFileInTheFolderOfTheCopy()
    {
        using var otherKey = SigningKey.New();
        var source = RealRelease(Signature(Own, _exe, otherKey));

        var exitCode = RealLocalCopy().Start(new LocalCopyDecision.CopyAndStart(source), new StartOptions(), _output, _errors);

        Assert.Equal(1, exitCode);
        Assert.Empty(_starter.Started);
        Assert.Empty(Directory.GetFiles(_place.Folder));
        Assert.Null(_share.TryRead());
    }

    [Fact]
    public void Start_ACopyExists_StartsItAsItIs()
    {
        _files.Existing.Add(_place.ExePath);

        var exitCode = LocalCopy().Start(new LocalCopyDecision.StartCopy(Source), new StartOptions(), _output, _errors);

        Assert.Equal(0, exitCode);
        Assert.Empty(_updates.Opened);
        Assert.Empty(_updates.Created);
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

    [Fact]
    public void IsTheExe_TheExeOfTheCopyInAnotherCase_IsTrue()
    {
        Assert.True(_place.IsTheExe(_place.ExePath.ToUpperInvariant()));
        Assert.True(_place.IsTheExe(Path.Combine(_place.Folder, "..", "Kiler", "kiler.exe")));
    }

    [Theory]
    [InlineData("Kiler.exe.old")]
    [InlineData("Kiler (2).exe")]
    [InlineData("Kiler.exe.new")]
    public void IsTheExe_AnotherExeInTheFolderOfTheCopy_IsFalse(string name)
    {
        Assert.False(_place.IsTheExe(Path.Combine(_place.Folder, name)));
    }

    [Fact]
    public void IsTheExe_TheExeOfTheSharedFolderOrNone_IsFalse()
    {
        Assert.False(_place.IsTheExe(Source));
        Assert.False(_place.IsTheExe(null));
    }

    private static string Lines(params string[] lines) => string.Concat(lines.Select(line => line + Environment.NewLine));

    private LocalCopy LocalCopy(string own = "1.0.0") => new(_place, _share, _files, _updates, _shortcuts, _starter, _key.PublicKey, own);

    /// <summary>The first copy on the real files; the shortcut and the start are still fakes.</summary>
    private LocalCopy RealLocalCopy()
    {
        var files = new CopyFiles();
        return new(_place, _share, files, files, _shortcuts, _starter, _key.PublicKey, "1.0.0");
    }

    private UpdateLeftovers Leftovers() => new(_place, _files, TimeProvider.System, _output, _errors, CancellationToken.None, TimeSpan.Zero);

    private string Signature(ReleaseVersion version, byte[] exe, SigningKey? key = null) => (key ?? _key).Sign(version, SHA256.HashData(exe));

    /// <summary>Puts a release on the fake shared folder: <c>version.json</c> and the exe.</summary>
    private void Release(ReleaseVersion version, byte[] exe, params string[] signatures)
    {
        _updates.Shared[ShareVersion] = new SharedFile(new VersionFile(version, SHA256.HashData(exe), signatures).ToJson());
        _updates.Shared[Source] = new SharedFile(exe);
    }

    /// <summary>A real shared folder, on the local disk, with the exe and a <c>version.json</c> of 1.0.0; the path of its exe.</summary>
    private string RealRelease(string signature)
    {
        var shareFolder = Path.Combine(_folder.Path, "share");
        Directory.CreateDirectory(shareFolder);
        var source = Path.Combine(shareFolder, "Kiler.exe");
        File.WriteAllBytes(source, _exe);
        File.WriteAllBytes(Path.Combine(shareFolder, "version.json"), new VersionFile(Own, SHA256.HashData(_exe), [signature]).ToJson());
        return source;
    }

    /// <summary>The start ended with 1 and the line before it made a copy; <paramref name="line"/> null when the test checks it.</summary>
    private void AssertRefusedBeforeTheCopy(string? line)
    {
        var exitCode = LocalCopy().Start(new LocalCopyDecision.CopyAndStart(Source), new StartOptions(), _output, _errors);

        Assert.Equal(1, exitCode);
        Assert.Empty(_updates.Created);
        AssertNothingDone();
        if (line is not null)
        {
            Assert.Equal(Lines(line), _errors.ToString());
        }
    }

    /// <summary>The start ended with 1 and the line, and deleted the copy it made while it still held it.</summary>
    private void AssertRefusedAfterTheCopy(string line)
    {
        var exitCode = LocalCopy().Start(new LocalCopyDecision.CopyAndStart(Source), new StartOptions(), _output, _errors);

        Assert.Equal(1, exitCode);
        var tmp = TmpPath;
        Assert.Equal([(tmp, true)], _updates.Deleted);
        Assert.True(_updates.Created[tmp].Disposed, "The copy was not let go.");
        Assert.Empty(_updates.Existing);
        AssertNothingDone();
        Assert.Equal(Lines(line), _errors.ToString());
    }

    /// <summary>A refusal of the first copy: no copy in place, no share.json, no shortcut and no start.</summary>
    private void AssertNothingDone()
    {
        Assert.DoesNotContain(_updates.Renamed, rename => rename.To == _place.ExePath);
        Assert.Null(_share.TryRead());
        Assert.Empty(_shortcuts.Created);
        Assert.Empty(_starter.Started);
        Assert.Equal(Lines(CopyLine), _output.ToString());
    }

    /// <summary>The files of the copy in memory; the leftovers only list and rename them here.</summary>
    private sealed class FakeFiles : ICopyFiles, ILeftoverFiles
    {
        public bool Network { get; set; } = true;

        public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(string From, string To)> Renamed { get; } = [];

        public bool IsOnNetwork(string path) => Network;

        public bool Exists(string path) => Existing.Contains(path);

        public IReadOnlyList<FolderFile> List(string folder) =>
        [
            .. Existing
                .Where(path => string.Equals(Path.GetDirectoryName(path), folder, StringComparison.OrdinalIgnoreCase))
                .Select(path => new FolderFile(path, FileAttributes.Archive, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)),
        ];

        public void Rename(string from, string to)
        {
            if (!Existing.Remove(from))
            {
                throw new FileNotFoundException("No such file in the fake folder.", from);
            }

            Existing.Add(to);
            Renamed.Add((from, to));
        }

        public void Delete(string path) => throw new NotSupportedException("A start from the shared folder deletes nothing.");

        public void DeleteUnheld(string path) => throw new NotSupportedException("A start from the shared folder deletes nothing.");
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

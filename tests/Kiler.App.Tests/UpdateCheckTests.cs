using System.Security.Cryptography;
using System.Text;
using Kiler.Core;
using Kiler.Crypto;

namespace Kiler.App.Tests;

/// <summary>
/// The check of a new version by the local copy (M3.6), with the files of the shared folder and the <c>.new</c> behind a
/// fake, and keys made by the tests. The share file is real, in a temporary folder. The real files have tests of their own
/// in <see cref="CopyFilesTests"/>, and one test here runs the check on them.
/// </summary>
public sealed class UpdateCheckTests : IDisposable
{
    private const string ShareFolder = @"\\server\share\Kiler";
    private const string ShareExe = ShareFolder + @"\Kiler.exe";
    private const string ShareVersion = ShareFolder + @"\version.json";

    /// <summary>The limit of the exe in the tests, so a file over it is small.</summary>
    private const int MaxExe = 300_000;

    private static readonly ReleaseVersion Next = new(1, 0, 1);

    private readonly TempFolder _folder = new();
    private readonly CopyPlace _place;
    private readonly ShareFile _share;
    private readonly FakeFiles _files = new();
    private readonly SigningKey _key = SigningKey.New();
    private readonly byte[] _exe = RandomNumberGenerator.GetBytes(200_000);
    private readonly StringWriter _output = new();
    private readonly StringWriter _errors = new();
    private readonly CancellationTokenSource _stop = new();

    public UpdateCheckTests()
    {
        _place = new CopyPlace(Path.Combine(_folder.Path, "Programs", "Kiler", "Kiler.exe"), Path.Combine(_folder.Path, "Start Menu", "Kiler.lnk"));
        _share = new ShareFile(Path.Combine(_folder.Path, "Kiler", "share.json"));
        _share.Write(ShareFolder);
    }

    /// <summary>The one <c>.new</c> that the check made.</summary>
    private string NewPath => Assert.Single(_files.Created).Key;

    private string CopyLine => $"Kiler 1.0.1 is in the shared folder. Kiler copies it to {_place.Folder} and checks it.";

    private static string NotSignedLine => $"{ShareExe} is not the Kiler 1.0.1 that version.json signs, so Kiler keeps its version.";

    public void Dispose()
    {
        _key.Dispose();
        _stop.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public void Run_AHigherVersionSignedWithItsKey_GivesTheCheckedNewFileKeptOpen()
    {
        Release(Next, _exe, Signature(Next, _exe));

        using var update = Check().Run();

        Assert.NotNull(update);
        Assert.Equal(Next, update.Version);
        Assert.Equal(NewPath, update.Path);
        var file = _files.Created[NewPath];
        Assert.True(file.ToArray().AsSpan().SequenceEqual(_exe), "The .new does not hold the exe of the shared folder.");
        Assert.False(file.Disposed, "The checked .new was let go before it is in place.");
        Assert.Empty(_files.Deleted);
        Assert.Equal(Lines(CopyLine, "Kiler 1.0.1 is copied and checked."), _output.ToString());
        Assert.Empty(_errors.ToString());

        update.Dispose();
        Assert.True(file.Disposed, "Disposing the update does not let the .new go.");
        Assert.Contains(NewPath, _files.Existing);
    }

    [Fact]
    public void Run_EachCheck_MakesItsNewFileUnderANewNameNextToTheCopy()
    {
        Release(Next, _exe, Signature(Next, _exe));

        using var first = Check().Run();
        using var second = Check().Run();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Path, second.Path);
        foreach (var path in new[] { first.Path, second.Path })
        {
            Assert.Equal(_place.Folder, Path.GetDirectoryName(path));
            Assert.Matches(@"^Kiler\.exe\.[0-9a-f]{32}\.new$", Path.GetFileName(path));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Run_TwoSignaturesOneOfThemWithItsKey_TakesTheRelease(int place)
    {
        using var otherKey = SigningKey.New();
        var signatures = new List<string> { Signature(Next, _exe, otherKey) };
        signatures.Insert(place, Signature(Next, _exe));
        Release(Next, _exe, [.. signatures]);

        using var update = Check().Run();

        Assert.NotNull(update);
        Assert.Contains(NewPath, _files.Existing);
    }

    [Fact]
    public void Run_NoVersionFile_CopiesNothingAndSaysSo()
    {
        _files.Shared[ShareExe] = new SharedFile(_exe);

        Assert.Null(Check().Run());

        Assert.Equal([ShareVersion], _files.Opened);
        Assert.Empty(_files.Created);
        Assert.Equal(Lines("The shared folder has no version.json, so Kiler takes no new version."), _output.ToString());
        Assert.Empty(_errors.ToString());
    }

    public static TheoryData<Exception> ReadErrors => new()
    {
        new IOException("The network path was not found."),
        new UnauthorizedAccessException("Access is denied."),
        new DirectoryNotFoundException("Could not find a part of the path."),
    };

    [Theory]
    [MemberData(nameof(ReadErrors))]
    public void Run_TheVersionFileCannotBeRead_CopiesNothingAndSaysSo(Exception error)
    {
        Release(Next, _exe, Signature(Next, _exe));
        _files.Shared[ShareVersion].OpenError = error;

        Assert.Null(Check().Run());

        Assert.Empty(_files.Created);
        Assert.Empty(_output.ToString());
        Assert.Equal(Lines($"Kiler cannot read {ShareVersion}, so it takes no new version now: {error.Message}"), _errors.ToString());
    }

    [Theory]
    [InlineData("1.0.1", "1.0.1")]
    [InlineData("1.0.2", "1.0.1")]
    [InlineData("1.10.0", "1.9.0")]
    [InlineData("2.0.0", "1.99.99")]
    public void Run_TheSameOrALowerVersion_CopiesNothingAndSaysNothing(string own, string sharedVersion)
    {
        Assert.True(ReleaseVersion.TryParse(sharedVersion, out var shared));
        Release(shared, _exe, Signature(shared, _exe));

        Assert.Null(Check(own).Run());

        Assert.Equal([ShareVersion], _files.Opened);
        Assert.Empty(_files.Created);
        Assert.Empty(_output.ToString());
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void Run_AHigherVersionByItsNumbers_IsTaken()
    {
        var shared = new ReleaseVersion(1, 10, 0);
        Release(shared, _exe, Signature(shared, _exe));

        using var update = Check("1.9.0").Run();

        Assert.NotNull(update);
        Assert.Equal(shared, update.Version);
    }

    [Theory]
    [InlineData("not json", "It is not JSON, or a value does not have the type it must have.")]
    [InlineData("""{"version": "1.0.1", "sha256": "00"}""", "sha256 is not a SHA-256 as lowercase hex.")]
    [InlineData("""{"version": "2", "signatures": []}""", "version is not major.minor.patch.")]
    public void Run_AVersionFileThatDoesNotFit_CopiesNothingAndSaysWhy(string json, string reason)
    {
        _files.Shared[ShareVersion] = new SharedFile(Encoding.UTF8.GetBytes(json));
        _files.Shared[ShareExe] = new SharedFile(_exe);

        Assert.Null(Check().Run());

        Assert.Equal([ShareVersion], _files.Opened);
        Assert.Empty(_files.Created);
        Assert.Equal(Lines($"{ShareVersion} does not fit, so Kiler takes no new version: {reason}"), _errors.ToString());
    }

    [Fact]
    public void Run_AVersionFileLargerThanTheLimit_ReadsOneByteOverItAndCopiesNothing()
    {
        var json = new VersionFile(Next, SHA256.HashData(_exe), [Signature(Next, _exe)]).ToJson();
        _files.Shared[ShareVersion] = new SharedFile([.. json, .. Enumerable.Repeat((byte)' ', 1024 * 1024)]);
        _files.Shared[ShareExe] = new SharedFile(_exe);

        Assert.Null(Check().Run());

        Assert.Equal(VersionFile.MaxSize + 1, _files.Shared[ShareVersion].BytesRead);
        Assert.Empty(_files.Created);
        Assert.Equal(Lines($"{ShareVersion} does not fit, so Kiler takes no new version: It is larger than {VersionFile.MaxSize} bytes."), _errors.ToString());
    }

    [Fact]
    public void Run_ASignatureThatIsNotOne_DeletesTheNewFileAndSaysSo()
    {
        Release(Next, _exe, Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)), "bm90IGEgc2lnbmF0dXJl");

        AssertRefused(NotSignedLine);
    }

    [Fact]
    public void Run_ASignatureOfAnotherKey_DeletesTheNewFileAndSaysSo()
    {
        using var otherKey = SigningKey.New();
        Release(Next, _exe, Signature(Next, _exe, otherKey));

        AssertRefused(NotSignedLine);
    }

    [Fact]
    public void Run_TheDeleteFailsWhileTheNewFileIsHeld_TriesAgainOnceItIsLetGo()
    {
        Release(Next, _exe, Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)));
        _files.DeleteFails.Enqueue(true);

        Assert.Null(Check().Run());

        Assert.Equal([(NewPath, true), (NewPath, false)], _files.Deleted);
        Assert.Empty(_files.Existing);
    }

    [Fact]
    public void Run_ADeleteThatFailsTwice_LeavesTheNewFileAndGoesOn()
    {
        Release(Next, _exe, Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)));
        _files.DeleteFails.Enqueue(true);
        _files.DeleteFails.Enqueue(true);

        Assert.Null(Check().Run());

        Assert.Equal([NewPath], _files.Existing);
        Assert.Equal(Lines(NotSignedLine), _errors.ToString());
    }

    [Fact]
    public void Run_AnotherExeThanTheSignedOne_DeletesTheNewFileAndSaysSo()
    {
        var signed = (byte[])_exe.Clone();
        signed[^1] ^= 1;
        Release(Next, _exe, Signature(Next, signed));

        AssertRefused(NotSignedLine);
    }

    [Fact]
    public void Run_ASignatureOfAnotherVersion_DeletesTheNewFileAndSaysSo()
    {
        Release(Next, _exe, Signature(new ReleaseVersion(1, 0, 2), _exe));

        AssertRefused(NotSignedLine);
    }

    [Fact]
    public void Run_AnExeLargerThanTheLimit_CopiesNothingAndSaysSo()
    {
        var exe = RandomNumberGenerator.GetBytes(MaxExe + 1);
        Release(Next, exe, Signature(Next, exe));

        Assert.Null(Check().Run());

        Assert.Empty(_files.Created);
        Assert.Equal(0, _files.Shared[ShareExe].BytesRead);
        Assert.Equal(Lines($"{ShareExe} is larger than {MaxExe} bytes, so Kiler takes no new version."), _errors.ToString());
    }

    [Fact]
    public void Run_AnExeWithMoreBytesThanItsLengthSays_StopsAtTheLimitAndDeletesTheNewFile()
    {
        var exe = RandomNumberGenerator.GetBytes(MaxExe + 1);
        Release(Next, exe, Signature(Next, exe));
        _files.Shared[ShareExe].Length = 1000;

        AssertRefused($"{ShareExe} is larger than {MaxExe} bytes, so Kiler takes no new version.");
        Assert.True(_files.Created[NewPath].ToArray().Length <= MaxExe, "More bytes than the limit were written.");
    }

    [Theory]
    [MemberData(nameof(ReadErrors))]
    public void Run_TheExeCannotBeOpened_CopiesNothingAndSaysSo(Exception error)
    {
        Release(Next, _exe, Signature(Next, _exe));
        _files.Shared[ShareExe].OpenError = error;

        Assert.Null(Check().Run());

        Assert.Empty(_files.Created);
        Assert.Equal(Lines($"Kiler cannot copy {ShareExe} to {_place.Folder}, so it takes no new version now: {error.Message}"), _errors.ToString());
    }

    [Fact]
    public void Run_TheCopyFailsHalfWay_DeletesTheNewFileAndSaysSo()
    {
        Release(Next, _exe, Signature(Next, _exe));
        var error = new IOException("The specified network name is no longer available.");
        _files.Shared[ShareExe].ReadError = error;

        AssertRefused($"Kiler cannot copy {ShareExe} to {_place.Folder}, so it takes no new version now: {error.Message}");
    }

    [Fact]
    public void Run_AnotherKilerHasTheNewFileOpen_DeletesNothingAndSaysSo()
    {
        Release(Next, _exe, Signature(Next, _exe));
        var error = new IOException("The process cannot access the file because it is being used by another process.");
        _files.CreateError = error;

        Assert.Null(Check().Run());

        Assert.Empty(_files.Deleted);
        Assert.Equal(Lines($"Kiler cannot copy {ShareExe} to {_place.Folder}, so it takes no new version now: {error.Message}"), _errors.ToString());
    }

    [Fact]
    public void Run_KilerStopsDuringTheCopy_DeletesTheNewFileAndSaysNothingMore()
    {
        Release(Next, _exe, Signature(Next, _exe));
        _files.Shared[ShareExe].OnSecondRead = _stop.Cancel;

        Assert.Null(Check().Run());

        Assert.Equal([(NewPath, true)], _files.Deleted);
        Assert.True(_files.Created[NewPath].Disposed, "The .new was not let go.");
        Assert.Equal(Lines(CopyLine), _output.ToString());
        Assert.Empty(_errors.ToString());
    }

    [Fact]
    public void Run_KilerStopsWhileTheVersionFileIsRead_SaysNothingAndCopiesNothing()
    {
        Release(Next, _exe, Signature(Next, _exe));
        _files.Shared[ShareVersion].OnSecondRead = _stop.Cancel;

        Assert.Null(Check().Run());

        Assert.Empty(_output.ToString());
        Assert.Empty(_errors.ToString());
        Assert.Empty(_files.Created);
    }

    [Fact]
    public void Run_KilerHasStopped_ReadsNothingAndSaysNothing()
    {
        Release(Next, _exe, Signature(Next, _exe));
        _stop.Cancel();

        Assert.Null(Check().Run());

        Assert.Empty(_files.Opened);
        Assert.Empty(_output.ToString());
        Assert.Empty(_errors.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("1.0.0-beta")]
    [InlineData("1.0.0+abc")]
    public void Run_ItsOwnVersionIsNotMajorMinorPatch_ReadsNothingAndSaysSo(string own)
    {
        Release(Next, _exe, Signature(Next, _exe));

        Assert.Null(Check(own).Run());

        Assert.Empty(_files.Opened);
        Assert.Equal(Lines($"Kiler does not look for a new version: its own version {own} is not major.minor.patch."), _errors.ToString());
    }

    [Fact]
    public void Run_NoSharedFolderKnown_ReadsNothingAndSaysSo()
    {
        File.Delete(_share.Path);

        Assert.Null(Check().Run());

        Assert.Empty(_files.Opened);
        Assert.Equal(Lines("Kiler does not look for a new version: it has not been started from the shared folder yet."), _output.ToString());
    }

    [Fact]
    public void Run_AFaultOfTheCode_IsALineAndNoException()
    {
        Release(Next, _exe, Signature(Next, _exe));
        _files.Shared[ShareVersion].OpenError = new InvalidOperationException("A fault of the code.");

        Assert.Null(Check().Run());

        Assert.StartsWith("Kiler could not look for a new version: System.InvalidOperationException: A fault of the code.", _errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_AFaultOfTheCodeAfterTheNewFileWasMade_DeletesIt()
    {
        Release(Next, _exe, Signature(Next, _exe));
        _files.Shared[ShareExe].ReadError = new InvalidOperationException("A fault of the code.");

        Assert.Null(Check().Run());

        Assert.Equal([(NewPath, true)], _files.Deleted);
        Assert.Empty(_files.Existing);
        Assert.StartsWith("Kiler could not look for a new version: System.InvalidOperationException", _errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_TheConsoleHasGone_StillChecks()
    {
        Release(Next, _exe, Signature(Next, _exe));
        using var gone = new GoneWriter();

        using var update = new UpdateCheck(_place, _share, _files, _key.PublicKey, "1.0.0", gone, gone, _stop.Token, MaxExe).Run();

        Assert.NotNull(update);
    }

    [Fact]
    public async Task StartInBackground_ChecksAndDeletesTheCheckedNewFileWhileItIsHeld()
    {
        Release(Next, _exe, Signature(Next, _exe));

        await Check().StartInBackground().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(Lines(CopyLine, "Kiler 1.0.1 is copied and checked."), _output.ToString());
        Assert.True(_files.Created[NewPath].Disposed, "The .new was not let go.");
        Assert.Equal([(NewPath, true)], _files.Deleted);
        Assert.Empty(_files.Existing);
    }

    [Fact]
    public async Task StartInBackground_OnTheRealFilesTwice_LeavesNoNewFile()
    {
        var shareFolder = Path.Combine(_folder.Path, "share");
        Directory.CreateDirectory(shareFolder);
        Directory.CreateDirectory(_place.Folder);
        File.WriteAllBytes(Path.Combine(shareFolder, "Kiler.exe"), _exe);
        File.WriteAllBytes(Path.Combine(shareFolder, "version.json"), new VersionFile(Next, SHA256.HashData(_exe), [Signature(Next, _exe)]).ToJson());
        _share.Write(shareFolder);

        for (var start = 0; start < 2; start++)
        {
            await new UpdateCheck(_place, _share, new CopyFiles(), _key.PublicKey, "1.0.0", _output, _errors, _stop.Token).StartInBackground()
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        Assert.Empty(_errors.ToString());
        Assert.Empty(Directory.GetFiles(_place.Folder));
    }

    [Fact]
    public async Task StartInBackground_AFaultOfTheCode_DoesNotFailTheTask()
    {
        _files.Shared[ShareVersion] = new SharedFile([]) { OpenError = new InvalidOperationException("A fault of the code.") };

        var task = Check().StartInBackground();
        await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.True(task.IsCompletedSuccessfully, "The task of the check failed.");
    }

    [Fact]
    public void Run_OnTheRealFiles_LeavesACheckedNewFileThatNoOneCanWriteWhileItIsOpen()
    {
        var shareFolder = Path.Combine(_folder.Path, "share");
        Directory.CreateDirectory(shareFolder);
        Directory.CreateDirectory(_place.Folder);
        File.WriteAllBytes(Path.Combine(shareFolder, "Kiler.exe"), _exe);
        File.WriteAllBytes(Path.Combine(shareFolder, "version.json"), new VersionFile(Next, SHA256.HashData(_exe), [Signature(Next, _exe)]).ToJson());
        _share.Write(shareFolder);

        string newPath;
        using (var update = new UpdateCheck(_place, _share, new CopyFiles(), _key.PublicKey, "1.0.0", _output, _errors, _stop.Token).Run())
        {
            Assert.NotNull(update);
            newPath = update.Path;
            Assert.Equal([newPath], Directory.GetFiles(_place.Folder));
            Assert.ThrowsAny<IOException>(() => CopyFilesTests.OpenToWrite(newPath).Dispose());
            Assert.True(CopyFilesTests.ReadWhileWritten(newPath).AsSpan().SequenceEqual(_exe), "The .new does not hold the exe of the shared folder.");
        }

        Assert.Empty(_errors.ToString());
        CopyFilesTests.OpenToWrite(newPath).Dispose();
    }

    [Fact]
    public void Run_OnTheRealFilesWithABadSignature_LeavesNoNewFile()
    {
        var shareFolder = Path.Combine(_folder.Path, "share");
        Directory.CreateDirectory(shareFolder);
        Directory.CreateDirectory(_place.Folder);
        File.WriteAllBytes(Path.Combine(shareFolder, "Kiler.exe"), _exe);
        using var otherKey = SigningKey.New();
        File.WriteAllBytes(Path.Combine(shareFolder, "version.json"), new VersionFile(Next, SHA256.HashData(_exe), [Signature(Next, _exe, otherKey)]).ToJson());
        _share.Write(shareFolder);

        Assert.Null(new UpdateCheck(_place, _share, new CopyFiles(), _key.PublicKey, "1.0.0", _output, _errors, _stop.Token).Run());

        Assert.Empty(Directory.GetFiles(_place.Folder));
    }

    private static string Lines(params string[] lines) => string.Concat(lines.Select(line => line + Environment.NewLine));

    private UpdateCheck Check(string own = "1.0.0") => new(_place, _share, _files, _key.PublicKey, own, _output, _errors, _stop.Token, MaxExe);

    private string Signature(ReleaseVersion version, byte[] exe, SigningKey? key = null) => (key ?? _key).Sign(version, SHA256.HashData(exe));

    /// <summary>Puts a release on the fake shared folder: <c>version.json</c> and the exe.</summary>
    private void Release(ReleaseVersion version, byte[] exe, params string[] signatures)
    {
        _files.Shared[ShareVersion] = new SharedFile(new VersionFile(version, SHA256.HashData(exe), signatures).ToJson());
        _files.Shared[ShareExe] = new SharedFile(exe);
    }

    /// <summary>The check gave nothing, with the line, and deleted the .new it made.</summary>
    private void AssertRefused(string line)
    {
        Assert.Null(Check().Run());

        var newPath = NewPath;
        Assert.True(_files.Created[newPath].Disposed, "The .new was not let go.");
        Assert.Equal([(newPath, true)], _files.Deleted);
        Assert.Empty(_files.Existing);
        Assert.Equal(Lines(CopyLine), _output.ToString());
        Assert.Equal(Lines(line), _errors.ToString());
    }

    /// <summary>A file of the fake shared folder, and what its stream does.</summary>
    private sealed class SharedFile(byte[] bytes)
    {
        public byte[] Bytes => bytes;

        public Exception? OpenError { get; set; }

        /// <summary>The length that its stream says, when it is not its own.</summary>
        public long? Length { get; set; }

        /// <summary>Thrown at the second read, so the copy has begun.</summary>
        public Exception? ReadError { get; set; }

        /// <summary>Done at the second read.</summary>
        public Action? OnSecondRead { get; set; }

        public long BytesRead { get; set; }
    }

    /// <summary>
    /// A read into a span comes here too: <see cref="MemoryStream"/> gives the read of a span of a derived type to
    /// <see cref="Stream"/>, which reads into an array; an override of both calls itself until the stack overflows.
    /// </summary>
    private sealed class SharedStream(SharedFile file) : MemoryStream(file.Bytes, writable: false)
    {
        private int _reads;

        public override long Length => file.Length ?? base.Length;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (++_reads == 2)
            {
                file.OnSecondRead?.Invoke();
                if (file.ReadError is not null)
                {
                    throw file.ReadError;
                }
            }

            var read = base.Read(buffer, offset, count);
            file.BytesRead += read;
            return read;
        }
    }

    /// <summary>The <c>.new</c> in memory; its bytes can be read after it is disposed.</summary>
    private sealed class NewFile : MemoryStream
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class FakeFiles : IUpdateFiles
    {
        public Dictionary<string, SharedFile> Shared { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Opened { get; } = [];

        public Exception? CreateError { get; set; }

        public Dictionary<string, NewFile> Created { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Each delete, and whether the file was still held then.</summary>
        public List<(string Path, bool Held)> Deleted { get; } = [];

        /// <summary>The deletes that fail, in order; the rest succeed.</summary>
        public Queue<bool> DeleteFails { get; } = new();

        public Stream OpenShared(string path)
        {
            Opened.Add(path);
            if (!Shared.TryGetValue(path, out var file))
            {
                throw new FileNotFoundException("No such file in the fake shared folder.", path);
            }

            if (file.OpenError is not null)
            {
                throw file.OpenError;
            }

            return new SharedStream(file);
        }

        public Stream CreateUpdate(string path)
        {
            if (CreateError is not null)
            {
                throw CreateError;
            }

            if (Existing.Contains(path))
            {
                throw new IOException("A file of that name is there.");
            }

            var file = new NewFile();
            Created[path] = file;
            Existing.Add(path);
            return file;
        }

        public bool TryDelete(string path)
        {
            Deleted.Add((path, Created.TryGetValue(path, out var file) && !file.Disposed));
            if (DeleteFails.TryDequeue(out var fails) && fails)
            {
                return false;
            }

            Existing.Remove(path);
            return true;
        }
    }

    /// <summary>A console that has gone: every line throws.</summary>
    private sealed class GoneWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value) => throw new IOException("The pipe has been ended.");
    }
}

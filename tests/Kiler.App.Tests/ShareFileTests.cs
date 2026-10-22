namespace Kiler.App.Tests;

public sealed class ShareFileTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly string _path;

    public ShareFileTests() => _path = Path.Combine(_folder.Path, "Kiler", "share.json");

    public void Dispose() => _folder.Dispose();

    [Theory]
    [InlineData(@"\\server\share\Kiler")]
    [InlineData(@"K:\Kiler")]
    [InlineData(@"\\server\споделено\Kiler")]
    public void Write_ThenTryRead_GivesTheFolder(string folder)
    {
        var file = new ShareFile(_path);

        file.Write(folder);

        Assert.Equal(folder, file.TryRead());
    }

    [Fact]
    public void Write_HoldsOnlyTheFolder_AndLeavesNoOtherFile()
    {
        new ShareFile(_path).Write(@"\\server\share\Kiler");

        Assert.Equal("""{"folder":"\\\\server\\share\\Kiler"}""", File.ReadAllText(_path));
        Assert.Equal([_path], Directory.GetFiles(Path.GetDirectoryName(_path)!));
    }

    [Fact]
    public void Write_ReplacesTheFolderFromBefore()
    {
        var file = new ShareFile(_path);

        file.Write(@"\\old-server\share\Kiler");
        file.Write(@"\\server\share\Kiler");

        Assert.Equal(@"\\server\share\Kiler", file.TryRead());
    }

    [Fact]
    public void TryRead_WithoutTheFileOrItsFolder_IsNull()
    {
        Assert.Null(new ShareFile(_path).TryRead());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"folder":null}""")]
    [InlineData("""{"folder":42}""")]
    [InlineData("""{"folder":""}""")]
    [InlineData("""{"folder":"Kiler"}""")]
    [InlineData("""{"folder":"\\Kiler"}""")]
    [InlineData("""{"folder":"K:Kiler"}""")]
    public void TryRead_AFileThatIsNotValid_IsNull(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, content);

        Assert.Null(new ShareFile(_path).TryRead());
    }
}

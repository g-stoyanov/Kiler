using Kiler.Crypto;

namespace Kiler.Release.Tests;

public class ExeVersionTests
{
    [Fact]
    public void TryRead_AnAssemblyOfTheCompiler_GivesItsInformationalVersionWithoutTheBuildMetadata()
    {
        Assert.True(ExeVersion.TryRead(File.ReadAllBytes(ToolFolder.ToolAssembly), out var version));

        Assert.Equal(ToolFolder.ToolVersion, version);
    }

    [Fact]
    public void ProductVersion_OfAnAssemblyOfTheCompiler_IsItsInformationalVersion()
    {
        Assert.True(ExeVersion.TryGetVersionResource(File.ReadAllBytes(ToolFolder.ToolAssembly), out var resource));

        Assert.Equal(PeBytes.ProductVersion, ExeVersion.ProductVersion(resource));
    }

    public static TheoryData<string, byte[]> NotPes => new()
    {
        { "nothing", [] },
        { "MZ alone", "MZ"u8.ToArray() },
        { "text", "Kiler 1.0.0"u8.ToArray() },
        { "the first 512 bytes of a PE", File.ReadAllBytes(ToolFolder.ToolAssembly)[..512] },
        { "the first half of a PE", File.ReadAllBytes(ToolFolder.ToolAssembly)[..(int)(new FileInfo(ToolFolder.ToolAssembly).Length / 2)] },
    };

    [Theory]
    [MemberData(nameof(NotPes))]
    public void TryRead_BytesThatAreNotAWholePe_IsFalse(string what, byte[] bytes)
    {
        Assert.False(ExeVersion.TryRead(bytes, out _), $"A version from {what}.");
    }

    [Fact]
    public void TryRead_APeWithoutResources_IsFalse()
    {
        var bytes = File.ReadAllBytes(ToolFolder.ToolAssembly);
        PeBytes.RemoveResourceDirectory(bytes);

        Assert.False(ExeVersion.TryRead(bytes, out _));
    }

    [Fact]
    public void TryRead_AnyByteOfTheResourcesChanged_NeverThrows()
    {
        var original = File.ReadAllBytes(ToolFolder.ToolAssembly);
        var headers = new System.Reflection.PortableExecutable.PEHeaders(new MemoryStream(original));
        var table = headers.PEHeader!.ResourceTableDirectory;
        Assert.True(headers.TryGetDirectoryOffset(table, out var start));

        var bytes = (byte[])original.Clone();
        for (var i = start; i < start + table.Size; i++)
        {
            foreach (var value in new byte[] { 0x00, 0xFF, (byte)(original[i] ^ 0x80) })
            {
                bytes[i] = value;
                _ = ExeVersion.TryRead(bytes, out _);
            }

            bytes[i] = original[i];
        }
    }

    [Fact]
    public void ProductVersion_AmongTheOtherStrings_IsFound()
    {
        var resource = VersionBlocks.Root(
            VersionBlocks.VarFileInfo(),
            VersionBlocks.StringFileInfo(VersionBlocks.Table(
                VersionBlocks.Text("CompanyName", "Kiler"),
                VersionBlocks.Text("FileVersion", "9.9.9.9"),
                VersionBlocks.Text("ProductVersion", "1.2.3+0123abcd"),
                VersionBlocks.Text("ProductName", "Kiler"))));

        Assert.Equal("1.2.3+0123abcd", ExeVersion.ProductVersion(resource));
    }

    [Fact]
    public void ProductVersion_WithTheLengthOfTheTextInBytes_IsFound()
    {
        var resource = VersionBlocks.Root(VersionBlocks.StringFileInfo(VersionBlocks.Table(VersionBlocks.Text("ProductVersion", "1.2.3", lengthInBytes: true))));

        Assert.Equal("1.2.3", ExeVersion.ProductVersion(resource));
    }

    [Fact]
    public void ProductVersion_OfTwoTablesThatAgree_IsFound()
    {
        var resource = VersionBlocks.Root(VersionBlocks.StringFileInfo(
            VersionBlocks.Table(VersionBlocks.Text("ProductVersion", "1.2.3")),
            VersionBlocks.Table(VersionBlocks.Text("ProductVersion", "1.2.3"))));

        Assert.Equal("1.2.3", ExeVersion.ProductVersion(resource));
    }

    public static TheoryData<string, byte[]> WithoutOneProductVersion => new()
    {
        {
            "two tables that differ",
            VersionBlocks.Root(VersionBlocks.StringFileInfo(
                VersionBlocks.Table(VersionBlocks.Text("ProductVersion", "1.2.3")),
                VersionBlocks.Table(VersionBlocks.Text("ProductVersion", "1.2.4"))))
        },
        { "no product version", VersionBlocks.Root(VersionBlocks.StringFileInfo(VersionBlocks.Table(VersionBlocks.Text("FileVersion", "1.2.3")))) },
        { "no StringFileInfo", VersionBlocks.Root(VersionBlocks.VarFileInfo()) },
        { "a product version outside StringFileInfo", VersionBlocks.Root(VersionBlocks.Table(VersionBlocks.Text("ProductVersion", "1.2.3"))) },
        { "another root", VersionBlocks.Block("VS_VERSION", [], 0, 0, VersionBlocks.StringFileInfo(VersionBlocks.Table(VersionBlocks.Text("ProductVersion", "1.2.3")))) },
        { "a block longer than the resource", VersionBlocks.Root(VersionBlocks.StringFileInfo(VersionBlocks.Table(VersionBlocks.Text("ProductVersion", "1.2.3"))))[..^4] },
        { "a key without its NUL", VersionBlocks.KeyWithoutNul() },
        { "nothing", [] },
    };

    [Theory]
    [MemberData(nameof(WithoutOneProductVersion))]
    public void ProductVersion_WithoutOneText_IsNull(string what, byte[] resource)
    {
        Assert.True(ExeVersion.ProductVersion(resource) is null, $"A product version from {what}.");
    }

    [Theory]
    [InlineData("1.0.0", 1, 0, 0)]
    [InlineData("1.2.3+0123abcd", 1, 2, 3)]
    [InlineData("10.20.30+", 10, 20, 30)]
    public void TryParse_AVersionOfThreeParts_GivesIt(string text, int major, int minor, int patch)
    {
        Assert.True(ExeVersion.TryParse(text, out var version));

        Assert.Equal(new ReleaseVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("+0123abcd")]
    [InlineData("1.0.0.0")]
    [InlineData("1.0")]
    [InlineData("1.0.0-beta")]
    [InlineData("1.0.0-beta+0123abcd")]
    [InlineData("v1.0.0")]
    [InlineData(" 1.0.0")]
    [InlineData("1.0.01")]
    public void TryParse_AnotherForm_IsFalse(string? text)
    {
        Assert.False(ExeVersion.TryParse(text, out _));
    }
}

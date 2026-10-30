namespace Kiler.DemoData.Tests;

public sealed class DemoOptionsTests
{
    [Theory]
    [InlineData(new[] { "dev-data" }, "dev-data", false, false)]
    [InlineData(new[] { "dev-data", "--replace" }, "dev-data", true, false)]
    [InlineData(new[] { "--replace", @"D:\Demo\data" }, @"D:\Demo\data", true, false)]
    [InlineData(new[] { "dev-data", "--volume" }, "dev-data", false, true)]
    [InlineData(new[] { "--volume", "dev-data", "--replace" }, "dev-data", true, true)]
    public void TryParse_TheDataFolderReplaceAndVolume_GivesThem(string[] args, string folder, bool replace, bool volume)
    {
        Assert.True(DemoOptions.TryParse(args, out var options, out var error), error);
        Assert.Equal(new DemoOptions(folder, replace, volume), options);
    }

    [Theory]
    [InlineData(new string[0], "Give the data folder.")]
    [InlineData(new[] { "--replace" }, "Give the data folder.")]
    [InlineData(new[] { " " }, "Give the data folder.")]
    [InlineData(new[] { "one", "two" }, "Give one data folder.")]
    [InlineData(new[] { "dev-data", "--replace", "--replace" }, "--replace is given twice.")]
    [InlineData(new[] { "--volume", "dev-data", "--volume" }, "--volume is given twice.")]
    [InlineData(new[] { "dev-data", "--force" }, "Unknown option: --force")]
    public void TryParse_ArgumentsThatDoNotFit_SaysWhy(string[] args, string expected)
    {
        Assert.False(DemoOptions.TryParse(args, out var options, out var error));
        Assert.Null(options);
        Assert.Equal(expected, error);
    }
}

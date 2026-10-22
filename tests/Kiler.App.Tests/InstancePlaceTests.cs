namespace Kiler.App.Tests;

public class InstancePlaceTests
{
    private const string LocalFolder = @"C:\Users\someone\AppData\Local\Kiler";
    private const int Session = 1;

    [Fact]
    public void For_WithoutData_IsTheFileOfTheSessionInTheLocalFolder()
    {
        var place = InstancePlace.For(null, LocalFolder, Session);

        Assert.Equal("Kiler.Instance", place.MutexName);
        Assert.Equal(Path.Combine(LocalFolder, "instance-1.json"), place.FilePath);
    }

    [Fact]
    public void For_AnotherSession_IsAnotherFileWithTheSameMutexName()
    {
        // The mutex name is the same, but Windows keeps a separate namespace per session.
        var first = InstancePlace.For(null, LocalFolder, 1);
        var second = InstancePlace.For(null, LocalFolder, 2);

        Assert.Equal(first.MutexName, second.MutexName);
        Assert.NotEqual(first.FilePath, second.FilePath);
        Assert.NotEqual(
            InstancePlace.For(@"D:\Demo\data", LocalFolder, 1).FilePath,
            InstancePlace.For(@"D:\Demo\data", LocalFolder, 2).FilePath);
    }

    [Fact]
    public void For_WithData_IsAnInstanceOfItsOwnInTheLocalFolder()
    {
        var place = InstancePlace.For(@"D:\Demo\data", LocalFolder, Session);

        Assert.Matches(@"^Kiler\.Instance\.[0-9a-f]{16}$", place.MutexName);
        Assert.Equal(LocalFolder, Path.GetDirectoryName(place.FilePath));
        Assert.Equal($"instance-1-{place.MutexName["Kiler.Instance.".Length..]}.json", Path.GetFileName(place.FilePath));
    }

    [Theory]
    [InlineData(@"d:\demo\DATA")]
    [InlineData(@"D:\Demo\data\")]
    [InlineData(@"D:\Demo\other\..\data")]
    [InlineData(@"D:/Demo/data")]
    public void For_AnotherSpellingOfTheSameFolder_IsTheSameInstance(string spelling)
    {
        Assert.Equal(InstancePlace.For(@"D:\Demo\data", LocalFolder, Session), InstancePlace.For(spelling, LocalFolder, Session));
    }

    [Fact]
    public void For_AnotherFolder_IsAnotherInstance()
    {
        var place = InstancePlace.For(@"D:\Demo\data", LocalFolder, Session);

        Assert.NotEqual(place, InstancePlace.For(@"D:\Demo\data2", LocalFolder, Session));
        Assert.NotEqual(place, InstancePlace.For(null, LocalFolder, Session));
    }

    [Fact]
    public void For_ARelativeFolder_IsTheFullPathFromTheCurrentFolder()
    {
        Assert.Equal(
            InstancePlace.For(Path.Combine(Environment.CurrentDirectory, "data"), LocalFolder, Session),
            InstancePlace.For("data", LocalFolder, Session));
    }
}

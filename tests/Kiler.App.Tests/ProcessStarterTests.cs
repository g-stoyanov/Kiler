using System.ComponentModel;

namespace Kiler.App.Tests;

public class ProcessStarterTests
{
    [Fact]
    public void StartInfo_StartsExactlyTheProgramInItsFolderWithTheArgumentsAndItsOutputUntouched()
    {
        string[] arguments = ["--no-browser", "--data", @"D:\demo data\"];

        var info = ProcessStarter.StartInfo(@"C:\Users\someone\AppData\Local\Programs\Kiler\Kiler.exe", arguments);

        Assert.Equal(@"C:\Users\someone\AppData\Local\Programs\Kiler\Kiler.exe", info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.Equal(@"C:\Users\someone\AppData\Local\Programs\Kiler", info.WorkingDirectory);
        Assert.Equal(arguments, info.ArgumentList);
        Assert.Empty(info.Arguments);

        // Nothing reads what the copy prints, the address with the token among it.
        Assert.False(info.RedirectStandardOutput);
        Assert.False(info.RedirectStandardError);
        Assert.False(info.RedirectStandardInput);
        Assert.False(info.CreateNoWindow);
    }

    [Fact]
    public void Start_AProgramThatIsMissing_ThrowsTheErrorThatLocalCopyReports()
    {
        using var folder = new TempFolder();

        Assert.Throws<Win32Exception>(() => new ProcessStarter().Start(Path.Combine(folder.Path, "Kiler.exe"), []));
    }
}

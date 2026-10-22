using System.Runtime.InteropServices;

namespace Kiler.App.Tests;

/// <summary>
/// The real shortcut, in a temporary folder instead of the Start menu. The shell reads it back through <c>WScript.Shell</c>,
/// with the built-in COM of the test process, which is not the code under test.
/// </summary>
public sealed class ShellShortcutsTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly string _target;

    public ShellShortcutsTests()
    {
        _target = Path.Combine(_folder.Path, "Programs", "Kiler", "Kiler.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(_target)!);
        File.WriteAllBytes(_target, []);
    }

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Create_MakesAShortcutThatStartsTheTargetInItsFolder()
    {
        var path = Path.Combine(_folder.Path, "Kiler.lnk");

        new ShellShortcuts().Create(path, _target);

        var (target, workingDirectory, description) = ReadShortcut(path);
        Assert.Equal(_target, target, ignoreCase: true);
        Assert.Equal(Path.GetDirectoryName(_target), workingDirectory, ignoreCase: true);
        Assert.Equal("Kiler", description);
    }

    [Fact]
    public void Create_OnAThreadThatIsAlreadyInASingleThreadedApartment_Works()
    {
        var path = Path.Combine(_folder.Path, "Kiler.lnk");
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                new ShellShortcuts().Create(path, _target);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);

        thread.Start();
        thread.Join();

        Assert.Null(error);
        Assert.Equal(_target, ReadShortcut(path).Target, ignoreCase: true);
    }

    [Fact]
    public void Create_InAFolderThatDoesNotExist_ThrowsAndMakesNothing()
    {
        var path = Path.Combine(_folder.Path, "missing", "Kiler.lnk");

        // LocalCopy.Start reports any error as a warning and starts the copy anyway.
        Assert.NotNull(Record.Exception(() => new ShellShortcuts().Create(path, _target)));
        Assert.False(File.Exists(path));
    }

    private static (string Target, string WorkingDirectory, string Description) ReadShortcut(string path)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not registered.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(path);
            return ((string)shortcut.TargetPath, (string)shortcut.WorkingDirectory, (string)shortcut.Description);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}

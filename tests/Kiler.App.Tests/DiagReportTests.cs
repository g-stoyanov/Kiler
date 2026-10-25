using System.Globalization;

namespace Kiler.App.Tests;

public sealed class DiagReportTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 30, 15, TimeSpan.Zero);

    private readonly TempFolder _folder = new();
    private readonly StringWriter _console = new();
    private readonly DiagReport _report;

    public DiagReportTests() => _report = new DiagReport(_console);

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Line_GoesToTheConsoleAtOnce_AndIntoTheFile()
    {
        _report.Line("one");
        Assert.Equal(Lines("one"), _console.ToString());

        _report.Line("two");
        Assert.Equal(Lines("one", "two"), _console.ToString());
        Assert.Equal(Lines("one", "two"), _report.ForFile());
    }

    [Fact]
    public void Check_WritesOkOrFail_AndOneFailureMarksTheReportFailed()
    {
        _report.Check("First", true);
        Assert.False(_report.Failed);

        _report.Check("Second", false, "why");
        _report.Check("Third", true, "detail");

        Assert.True(_report.Failed);
        Assert.Equal(Lines("[OK] First", "[FAIL] Second: why", "[OK] Third: detail"), _report.ForFile());
    }

    [Fact]
    public void Timing_HasTheTimeAndTheTimePerFile_WithADecimalPointInEveryCulture()
    {
        var culture = CultureInfo.CurrentCulture;
        // Without the overrides of this machine, which may have changed the decimal comma of bg-BG.
        CultureInfo.CurrentCulture = new CultureInfo("bg-BG", useUserOverride: false);
        try
        {
            _report.Timing("Write", 2000, TimeSpan.FromMilliseconds(4567.8));
            _report.Line($"Check: {DiagReport.Ms(TimeSpan.FromMilliseconds(1234.4))}");
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        Assert.Equal(Lines("Write: 2000 files in 4568 ms (2.28 ms per file)", "Check: 1234 ms"), _report.ForFile());
    }

    [Fact]
    public void Timing_NoFiles_HasNoTimePerFile()
    {
        _report.Timing("Delete the files", 0, TimeSpan.FromMilliseconds(3));

        Assert.Equal(Lines("Delete the files: no files in 3 ms"), _report.ForFile());
    }

    [Fact]
    public void ForFile_HidesAPathInAnyLetterCase_WithOrWithoutItsEndingSeparator_AndTheConsoleKeepsIt()
    {
        _report.HidePath(@"\\server\share\Kiler\", "<exe folder>");
        _report.Line(@"Exe: \\SERVER\Share\kiler\Kiler.exe");
        _report.Line(@"Folder: \\server\share\Kiler");

        Assert.Equal(Lines(@"Exe: <exe folder>\Kiler.exe", "Folder: <exe folder>"), _report.ForFile());
        Assert.Equal(Lines(@"Exe: \\SERVER\Share\kiler\Kiler.exe", @"Folder: \\server\share\Kiler"), _console.ToString());
    }

    [Fact]
    public void ForFile_AFolderInsideAnotherHiddenOne_GetsItsOwnPlaceholder_WhateverTheOrder()
    {
        _report.HidePath(@"C:\Users\demo.user", "<profile>");
        _report.HidePath(@"C:\Users\demo.user\AppData\Local\Temp\run", "<test folder>");
        _report.Line(@"C:\Users\demo.user\AppData\Local\Temp\run\x and C:\Users\demo.user\Desktop");

        Assert.Equal(Lines(@"<test folder>\x and <profile>\Desktop"), _report.ForFile());
    }

    [Fact]
    public void ForFile_OfTwoEqualValues_TakesTheLaterPlaceholder()
    {
        // `--diag` with the folder of the exe: the exe folder is registered first, the test folder later.
        _report.HidePath(@"\\server\share\Kiler", "<exe folder>");
        _report.HidePath(@"\\server\share\Kiler\", "<test folder>");
        _report.Line(@"Test folder: \\server\share\Kiler");

        Assert.Equal(Lines("Test folder: <test folder>"), _report.ForFile());
    }

    [Theory]
    [InlineData("file", "<user>")]
    [InlineData("File.", "<user>.")]
    [InlineData(@"DEMO\file", @"DEMO\<user>")]
    [InlineData("2000 files", "2000 files")]
    [InlineData("profile", "profile")]
    [InlineData("file2", "file2")]
    [InlineData("_file_", "_<user>_")]
    public void ForFile_HidesANameOnlyWhereItIsNotPartOfALongerWord(string line, string hidden)
    {
        _report.HideName("file", "<user>");
        _report.Line(line);

        Assert.Equal(Lines(hidden), _report.ForFile());
    }

    [Fact]
    public void ForFile_HidesACyrillicNameInAnyLetterCase_ButNotInsideALongerWord()
    {
        _report.HideName("иван", "<user>");
        _report.Line("ИВАН, Иван, иванов");

        Assert.Equal(Lines("<user>, <user>, иванов"), _report.ForFile());
    }

    [Fact]
    public void ForFile_DoesNotHideAPlaceholderAgain()
    {
        // A user named "test" must not cut the placeholder of the test folder.
        _report.HidePath(@"C:\Test", "<test folder>");
        _report.HideName("test", "<user>");
        _report.Line(@"C:\Test\x by test");

        Assert.Equal(Lines(@"<test folder>\x by <user>"), _report.ForFile());
    }

    [Fact]
    public void ForFile_TakesTheCharactersOfAValueLiterally()
    {
        _report.HideName("a.b", "<user>");
        _report.HidePath(@"C:\x+y (1)", "<test folder>");
        _report.Line(@"a.b axb C:\x+y (1)\z C:\xxy 1");

        Assert.Equal(Lines(@"<user> axb <test folder>\z C:\xxy 1"), _report.ForFile());
    }

    [Fact]
    public void ForFile_IgnoresBlankValues()
    {
        _report.HideName("", "<user>");
        _report.HideName(" ", "<user>");
        _report.HidePath("", "<exe folder>");
        _report.Line("a b");

        Assert.Equal(Lines("a b"), _report.ForFile());
    }

    [Fact]
    public void Save_WritesTheTextOfTheFileUnderANewName_AndSaysWhereOnTheConsole()
    {
        _report.HideName("demo.user", "<user>");
        _report.Line("Run by demo.user");

        var first = _report.Save(_folder.Path, Now);
        var second = _report.Save(_folder.Path, Now);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        foreach (var path in new[] { first, second })
        {
            Assert.Equal(_folder.Path, Path.GetDirectoryName(path));
            Assert.Matches(@"^Kiler-diag-20261025-093015-[0-9a-f]{4}\.txt$", Path.GetFileName(path));
            Assert.Equal(Lines("Run by <user>"), File.ReadAllText(path));
            Assert.Contains($"Report saved: {path}", _console.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Save_IntoAFolderThatDoesNotExist_SaysSoOnTheConsole_AndGivesNull()
    {
        var missing = Path.Combine(_folder.Path, "missing");

        Assert.Null(_report.Save(missing, Now));
        Assert.StartsWith($"Kiler could not save the report in {missing}: ", _console.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(missing));
    }

    private static string Lines(params string[] lines) => string.Concat(lines.Select(line => line + Environment.NewLine));
}

using Kiler.Crypto;
using Kiler.Storage;

namespace Kiler.Core.Tests;

/// <summary>The documents of a new Kiler, which the wizard makes and writes through the local copy (M4.3).</summary>
public sealed class NewKilerTests : IDisposable
{
    private const string Account = "demo.admin";

    // Made up, and compared only with Assert.True (SKILL.md, "Tests").
    private const string DemoPassword = "demo-password-1";

    private static readonly string[] Names = ["Guest", "Team", "Prod"];

    private readonly TestData _data = new();

    public static TheoryData<string[]?, string?, string?, int, string> ValuesThatDoNotFit => new()
    {
        { null, "Demo Admin", DemoPassword, 30, "levels is missing." },
        { ["Guest"], "Demo Admin", DemoPassword, 30, "The levels are not 0 to a top level from 1 to 9." },
        { [.. Enumerable.Range(0, 11).Select(level => $"Level {level}")], "Demo Admin", DemoPassword, 30, "The levels are not 0 to a top level from 1 to 9." },
        { ["Guest", null!, "Prod"], "Demo Admin", DemoPassword, 30, "Entry 1 of levels is null." },
        { ["Guest", " ", "Prod"], "Demo Admin", DemoPassword, 30, "The name of level 1 is blank or longer than 40 characters." },
        { ["Guest", new string('t', 41)], "Demo Admin", DemoPassword, 30, "The name of level 1 is blank or longer than 40 characters." },
        { ["Guest", "Team", "TEAM"], "Demo Admin", DemoPassword, 30, "Level 2 has the name of a level below it, without regard to case." },
        { Names, null, DemoPassword, 30, "displayName is missing." },
        { Names, " ", DemoPassword, 30, "displayName is blank or longer than 100 characters." },
        { Names, new string('d', 101), DemoPassword, 30, "displayName is blank or longer than 100 characters." },
        { Names, "Demo Admin", null, 30, "password is missing." },
        { Names, "Demo Admin", "demo-pw", 30, "A password has 8 to 1024 characters." },
        { Names, "Demo Admin", new string('p', 1025), 30, "A password has 8 to 1024 characters." },
        { Names, "Demo Admin", DemoPassword, 0, "autoLockMinutes is not from 1 to 1440." },
        { Names, "Demo Admin", DemoPassword, 1441, "autoLockMinutes is not from 1 to 1440." },
    };

    public void Dispose() => _data.Dispose();

    [Fact]
    public void MakeAndWrite_MakeTheDocuments_WhoseKeysUnlockWithThePasswordAndTheRecoveryKey_AndVerify()
    {
        var kiler = NewKiler.Make(Account, Names, "Demo Admin", DemoPassword, guestMode: true, autoLockMinutes: 45);

        var written = kiler.Write(_data.Copy);

        Assert.Equal(new NewKilerWrite(NewKilerOutcome.Made, null), written);
        var files = new Team(_data.Copy).Files;
        var levels = Valid(files.Levels);
        Assert.Equal(Names, levels.Levels.Select(level => level.Name));
        Assert.Null(levels.Levels[0].Check);
        var settings = Valid(files.Settings);
        Assert.True(settings.GuestMode);
        Assert.Equal(45, settings.AutoLockMinutes);
        Assert.Equal([Account], files.Users.Keys);
        var admin = Valid(files.Users[Account]);
        Assert.Equal(("Demo Admin", 2, true, true, false), (admin.DisplayName, admin.Level, admin.CanEdit, admin.IsAdmin, admin.MustChangePassword));

        // Both sets verify against the same checks, so they hold the same keys.
        AssertEveryLevelVerifies(levels, LevelKeys.Unlock(admin.Keys, DemoPassword));
        Assert.True(RecoveryKey.TryParse(kiler.RecoveryKey, out var recoveryKey), "The recovery key is not in the form that is shown.");
        using (recoveryKey)
        {
            AssertEveryLevelVerifies(levels, LevelKeys.Unlock(levels.Recovery, recoveryKey));
        }
    }

    [Fact]
    public void Write_WhenLevelsJsonIsThere_WritesNothing_AsTheSecondOfTwoWizards()
    {
        var first = NewKiler.Make(Account, Names, "Demo Admin", DemoPassword, guestMode: false, autoLockMinutes: 30);
        var second = NewKiler.Make("other.admin", ["Guest", "Team"], "Other Admin", DemoPassword, guestMode: true, autoLockMinutes: 10);
        Assert.Equal(NewKilerOutcome.Made, first.Write(_data.Copy).Outcome);

        // The Kiler of another user, whose copy has not loaded what the first wrote.
        var written = second.Write(_data.OpenCopy());

        Assert.Equal(new NewKilerWrite(NewKilerOutcome.Exists, "levels.json was not written, as it is there already."), written);
        var files = Load();
        Assert.Equal(Names, Valid(files.Levels).Levels.Select(level => level.Name));
        Assert.Equal(30, Valid(files.Settings).AutoLockMinutes);
        Assert.Equal([Account], files.Users.Keys);

        // Each wizard makes new keys and a new recovery key.
        Assert.NotEqual(first.Levels.Levels[1].Check, second.Levels.Levels[1].Check);
        Assert.False(first.RecoveryKey == second.RecoveryKey, "Two wizards made the same recovery key.");
    }

    [Fact]
    public void Write_WhileTheCopyIsOffline_WritesNothing()
    {
        var kiler = NewKiler.Make(Account, Names, "Demo Admin", DemoPassword, guestMode: false, autoLockMinutes: 30);
        var away = $"{_data.Folder}-away";
        Directory.Move(_data.Folder, away);
        Assert.NotNull(Xunit.Record.Exception(() => _data.Copy.Load()));
        Assert.True(_data.Copy.Offline, "The copy is not offline.");

        var written = kiler.Write(_data.Copy);

        Assert.Equal(new NewKilerWrite(NewKilerOutcome.Offline, "levels.json was not written, as the data folder cannot be reached."), written);
        Directory.Move(away, _data.Folder);
        Assert.Empty(Directory.GetFiles(_data.Folder, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Write_WhenALaterDocumentCannotBeWritten_StopsThere_AndIsPartial()
    {
        Assert.Equal(WriteOutcome.Written, _data.OpenCopy().Create(SettingsFile.Key, new SettingsFile(guestMode: false, autoLockMinutes: 30).ToContent()).Outcome);
        var kiler = NewKiler.Make(Account, Names, "Demo Admin", DemoPassword, guestMode: true, autoLockMinutes: 45);

        var written = kiler.Write(_data.Copy);

        Assert.Equal(new NewKilerWrite(NewKilerOutcome.Partial, "settings.json was not written, as it is there already."), written);
        var files = Load();
        Assert.NotNull(files.Levels);
        Assert.Equal(30, Valid(files.Settings).AutoLockMinutes);
        Assert.Empty(files.Users);
    }

    [Theory]
    [MemberData(nameof(ValuesThatDoNotFit))]
    public void TryCheck_RefusesValuesThatDoNotFit_WithTheReason(string[]? names, string? displayName, string? password, int minutes, string reason)
    {
        Assert.False(NewKiler.TryCheck(names, displayName, password, minutes, out var invalid));
        Assert.Equal(reason, invalid);
    }

    [Fact]
    public void TryCheck_TakesTheLimits()
    {
        Assert.True(NewKiler.TryCheck(["Guest", "Team"], "D", new string('p', Password.MinLength), 1, out var invalid), invalid);
        Assert.True(
            NewKiler.TryCheck([.. Enumerable.Range(0, LevelsFile.MaxTop + 1).Select(level => new string('l', LevelsFile.MaxNameLength - 1) + level)], new string('d', UserFile.MaxDisplayNameLength), new string('p', Password.MaxLength), SettingsFile.MaxAutoLockMinutes, out invalid),
            invalid);
    }

    [Fact]
    public void Make_RefusesValuesThatDoNotFit_BeforeAKeyIsMade()
    {
        Assert.Equal("A password has 8 to 1024 characters.", Assert.Throws<ArgumentException>(() => NewKiler.Make(Account, Names, "Demo Admin", "demo-pw", guestMode: false, autoLockMinutes: 30)).Message);
        Assert.Throws<ArgumentException>(() => NewKiler.Make("demo admin", Names, "Demo Admin", DemoPassword, guestMode: false, autoLockMinutes: 30));
    }

    private static T Valid<T>(TeamFile<T>? file)
        where T : class
    {
        Assert.NotNull(file);
        Assert.True(file.IsValid, file.Invalid);
        return file.Model;
    }

    /// <summary>Checks that the keys are those of the levels 1 to the top level, each verified by its check, and disposes them.</summary>
    private static void AssertEveryLevelVerifies(LevelsFile levels, IReadOnlyDictionary<int, SecretKey> keys)
    {
        try
        {
            Assert.Equal(Enumerable.Range(1, levels.Top), keys.Keys.Order());
            foreach (var (level, key) in keys)
            {
                var check = levels.Levels[level].Check;
                Assert.NotNull(check);
                Assert.True(LevelCheck.Verify(key, check, level), $"The key of level {level} does not verify.");
            }
        }
        finally
        {
            foreach (var key in keys.Values)
            {
                key.Dispose();
            }
        }
    }

    /// <summary>The team as the share has it, through a new copy.</summary>
    private TeamFiles Load()
    {
        var copy = _data.OpenCopy();
        copy.Load();
        return new Team(copy).Files;
    }
}

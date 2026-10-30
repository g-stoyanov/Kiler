using Kiler.Core;
using Kiler.Storage;
using Microsoft.Win32.SafeHandles;

namespace Kiler.DemoData.Tests;

/// <summary>
/// The demo Kiler (M4.10): the data folder that <see cref="DemoKiler.Make"/> writes opens through a new local copy, as Kiler
/// opens it at its first start, and each demo user signs in with their demo password. It is made once for the class, as each
/// user costs three PBKDF2.
/// </summary>
public sealed class DemoKilerTests(DemoKilerTests.Made made) : IClassFixture<DemoKilerTests.Made>
{
    public static TheoryData<string> Accounts => [.. DemoKiler.Users.Select(user => user.Account)];

    [Fact]
    public void Make_WritesADataFolderThatOpens_WithTheDemoLevelsSettingsAndUsers()
    {
        var files = made.Files;

        var levels = Assert.IsType<LevelsFile>(files.Levels?.Model);
        Assert.Equal(DemoKiler.LevelNames, levels.Levels.Select(level => level.Name));
        var settings = Assert.IsType<SettingsFile>(files.Settings?.Model);
        Assert.Equal((true, 30), (settings.GuestMode, settings.AutoLockMinutes));
        Assert.Equal(DemoKiler.Users.Select(user => user.Account).Order(StringComparer.Ordinal), files.Users.Keys);
        Assert.All(files.Users.Values, file => Assert.True(file.IsValid, file.Invalid));

        // The initial templates come with the wizard (M5.2), at the levels of the demo, 0 to 3.
        Assert.Equal(["db", "link", "note", "sql"], files.Templates.Keys);
        Assert.All(files.Templates.Values, file => Assert.True(file.IsValid, file.Invalid));
        var password = Assert.Single(Assert.IsType<TemplateFile>(files.Templates["db"].Model).Fields, field => field.Key == "password");
        Assert.Equal(3, password.LevelByEnv?["prod"]);
    }

    [Theory]
    [MemberData(nameof(Accounts))]
    public void EachDemoUser_SignsInWithTheirDemoPassword_WithTheirLevelAndRights_AndNoChange(string account)
    {
        var user = Assert.Single(DemoKiler.Users, user => user.Account == account);

        Assert.True(SignIn.TryStart(made.Files, account, user.Password, out var session, out var refusal), $"{account} did not sign in: {refusal}.");
        using (session)
        {
            // Level 0 has no key, and every session reads it.
            Assert.Equal(Enumerable.Range(0, user.Level + 1), session.Levels);
            Assert.Equal((user.DisplayName, user.CanEdit, user.IsAdmin, false), (session.DisplayName, session.CanEdit, session.IsAdmin, session.MustChangePassword));
        }
    }

    [Fact]
    public void ADemoUser_WithAnotherDemoPassword_DoesNotSignIn()
    {
        Assert.False(SignIn.TryStart(made.Files, "demo.reader", DemoKiler.Admin.Password, out _, out var refusal));
        Assert.Equal(SignInRefusal.WrongPassword, refusal);
    }

    /// <summary>The demo Kiler in a temporary folder, read through a local copy of its own; the folder is deleted with it.</summary>
    public sealed class Made : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "Kiler.Tests", Guid.NewGuid().ToString("N"));

        public Made()
        {
            var data = Path.Combine(_folder, "data");
            var work = Directory.CreateDirectory(Path.Combine(_folder, "work")).FullName;
            DemoKiler.Make(data, work);

            var copy = DataCopy.Open(DataFolder.Open(data, new NoOwners()), Directory.CreateDirectory(Path.Combine(_folder, "copy")).FullName, TimeProvider.System);
            var load = copy.Load();
            Assert.Empty(load.Outside);
            Assert.Empty(load.Skipped);
            Files = new Team(copy).Files;
        }

        public TeamFiles Files { get; }

        public void Dispose() => Directory.Delete(_folder, recursive: true);

        private sealed class NoOwners : IFileOwner
        {
            public string? Of(SafeFileHandle file) => null;
        }
    }
}

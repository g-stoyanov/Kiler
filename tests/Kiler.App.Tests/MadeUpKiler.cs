using System.Text.Json.Nodes;
using Kiler.Core;
using Kiler.Crypto;

namespace Kiler.App.Tests;

/// <summary>
/// The documents of a made-up Kiler for the sign-in (M4.4): the levels Guest, Team and Prod, and user files locked with
/// <see cref="Password"/>. They are made once per run, as each lock runs PBKDF2, and a test writes copies of them into a
/// data folder of its own (<see cref="WriteTo"/>).
/// </summary>
internal static class MadeUpKiler
{
    /// <summary>Made up, and compared only with <c>Assert.True</c> (SKILL.md, "Tests").</summary>
    public const string Password = "demo-password-1";

    /// <summary>The top level, its key, isAdmin and canEdit.</summary>
    public const string Admin = "demo.admin";

    /// <summary>Level 1.</summary>
    public const string User = "demo.user";

    /// <summary>Level 1, canEdit.</summary>
    public const string Editor = "demo.editor";

    /// <summary>Level 1, with a temporary password.</summary>
    public const string Temporary = "demo.temp";

    /// <summary>A user of level 1 who wrote the top level, isAdmin and canEdit into their own file.</summary>
    public const string Raised = "demo.raised";

    /// <summary>A user who added themselves, with keys of their own locked with <see cref="Password"/>.</summary>
    public const string Stranger = "demo.stranger";

    private static readonly Lazy<Documents> Made = new(Make);

    /// <summary>The check of level 1 in <c>levels.json</c>.</summary>
    public static string CheckOfLevel1 => Made.Value.Levels.Levels[1].Check!;

    /// <summary>
    /// Writes <c>levels.json</c>, <c>settings.json</c> and the user files into the data folder of <paramref name="data"/>:
    /// the settings with <paramref name="guestMode"/> and 30 minutes of the automatic lock, or <paramref name="settings"/>.
    /// </summary>
    public static MadeUpData WriteTo(MadeUpData data, bool guestMode, JsonObject? settings = null)
    {
        var made = Made.Value;
        data.Create(LevelsFile.Key, made.Levels.ToContent());
        data.Create(SettingsFile.Key, settings ?? new SettingsFile(guestMode, autoLockMinutes: 30).ToContent());
        foreach (var user in made.Users)
        {
            data.Create(user.Key, user.ToContent());
        }

        return data;
    }

    /// <summary>The user file of <paramref name="account"/>, one of the accounts above.</summary>
    public static UserFile UserOf(string account) => Made.Value.Users.Single(user => user.Account == account);

    private static Documents Make()
    {
        using var team = SecretKey.New();
        using var prod = SecretKey.New();
        using var own = SecretKey.New();
        var levels = new LevelsFile(
            [new(0, "Guest", null), new(1, "Team", LevelCheck.Create(team, 1)), new(2, "Prod", LevelCheck.Create(prod, 2))],
            new LockedKeys("salt", new Dictionary<int, string>()));
        var one = new Dictionary<int, SecretKey> { [1] = team };
        UserFile[] users =
        [
            NewUser(Admin, 2, canEdit: true, isAdmin: true, mustChangePassword: false, new Dictionary<int, SecretKey> { [1] = team, [2] = prod }),
            NewUser(User, 1, canEdit: false, isAdmin: false, mustChangePassword: false, one),
            NewUser(Editor, 1, canEdit: true, isAdmin: false, mustChangePassword: false, one),
            NewUser(Temporary, 1, canEdit: false, isAdmin: false, mustChangePassword: true, one),
            NewUser(Raised, 2, canEdit: true, isAdmin: true, mustChangePassword: false, new Dictionary<int, SecretKey> { [1] = team, [2] = own }),
            NewUser(Stranger, 1, canEdit: false, isAdmin: false, mustChangePassword: false, new Dictionary<int, SecretKey> { [1] = own }),
        ];
        return new(levels, users);
    }

    private static UserFile NewUser(string account, int level, bool canEdit, bool isAdmin, bool mustChangePassword, Dictionary<int, SecretKey> keys) =>
        new(account, $"Demo {account}", level, canEdit, isAdmin, mustChangePassword, LevelKeys.Lock(keys, Password));

    private sealed record Documents(LevelsFile Levels, UserFile[] Users);
}

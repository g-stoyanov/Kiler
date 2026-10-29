using System.Text.Json.Nodes;
using Kiler.Crypto;
using Kiler.Storage;

namespace Kiler.Core.Tests;

/// <summary>
/// A made-up Kiler for the sign-in (M4.4): a key of each level from 1 to <see cref="Top"/>, <c>levels.json</c> with their
/// checks, and user files with some of them locked with a password. The files of the team come through the data layer, as
/// Kiler reads them. Each user file runs PBKDF2 once.
/// </summary>
internal sealed class TestKiler : IDisposable
{
    /// <summary>Made up, and compared only with <c>Assert.True</c> (SKILL.md, "Tests").</summary>
    public const string Password = "demo-password-1";

    private readonly Dictionary<int, SecretKey> _keys = [];

    public TestKiler(int top = 3)
    {
        Top = top;
        var entries = new List<LevelEntry> { new(0, "Guest", null) };
        for (var level = 1; level <= top; level++)
        {
            var key = SecretKey.New();
            _keys.Add(level, key);
            entries.Add(new(level, $"Level {level}", LevelCheck.Create(key, level)));
        }

        Levels = new LevelsFile(entries, new LockedKeys("salt", new Dictionary<int, string>()));
    }

    public int Top { get; }

    public LevelsFile Levels { get; }

    /// <summary>The check of <paramref name="level"/> in <see cref="Levels"/>.</summary>
    public string Check(int level) => Levels.Levels[level].Check!;

    /// <summary>
    /// A user file of <paramref name="account"/> with the keys of <paramref name="keyLevels"/> (by default 1 to
    /// <paramref name="level"/>) and the keys of <paramref name="own"/>, which are not the keys of their levels, locked
    /// together with <see cref="Password"/>.
    /// </summary>
    public UserFile User(
        string account,
        int level,
        bool canEdit = false,
        bool isAdmin = false,
        bool mustChangePassword = false,
        int[]? keyLevels = null,
        IReadOnlyDictionary<int, SecretKey>? own = null)
    {
        var keys = (keyLevels ?? Enumerable.Range(1, level).ToArray()).ToDictionary(key => key, key => _keys[key]);
        foreach (var (ownLevel, key) in own ?? new Dictionary<int, SecretKey>())
        {
            keys[ownLevel] = key;
        }

        return new UserFile(account, $"Demo {account}", level, canEdit, isAdmin, mustChangePassword, LevelKeys.Lock(keys, Password));
    }

    /// <summary>The files of the team with <paramref name="users"/>, <see cref="Levels"/>, and settings with or without guest mode.</summary>
    public TeamFiles Files(bool guestMode, params UserFile[] users) =>
        Files(Levels.ToContent(), new SettingsFile(guestMode, autoLockMinutes: 30).ToContent(), users.Select(user => (user.Key, user.ToContent())));

    /// <summary>The files of the team as the data layer reads these contents; a content that is null is not written.</summary>
    public static TeamFiles Files(JsonObject? levels, JsonObject? settings, IEnumerable<(DocumentKey Key, JsonObject Content)> users)
    {
        using var data = new TestData();
        if (levels is not null)
        {
            data.WriteAndRead(LevelsFile.Key, levels);
        }

        if (settings is not null)
        {
            data.WriteAndRead(SettingsFile.Key, settings);
        }

        foreach (var (key, content) in users)
        {
            data.WriteAndRead(key, content);
        }

        return new Team(data.Copy).Files;
    }

    public void Dispose()
    {
        foreach (var key in _keys.Values)
        {
            key.Dispose();
        }
    }
}

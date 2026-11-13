using System.Text.Json.Nodes;
using Kiler.Crypto;
using Kiler.Storage;

namespace Kiler.Core.Tests;

/// <summary>
/// A made-up Kiler for the sign-in (M4.4): a key of each level from 1 to <see cref="Top"/>, <c>levels.json</c> with their
/// checks, sealed by <see cref="AdminKey"/> for version 1 (M4.14), and user files with some of them locked with a password.
/// The files of the team come through the data layer, as Kiler reads them. Each user file runs PBKDF2 once.
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

        // The version of a create, as the files of the team are written.
        Levels = new LevelsFile(entries, new LockedKeys("salt", new Dictionary<int, string>())).Sealed(AdminKey, 1);
    }

    public int Top { get; }

    /// <summary><c>levels.json</c>, sealed for version 1: its seal verifies once it is created, and fails at any later version.</summary>
    public LevelsFile Levels { get; }

    /// <summary>The admin key of the made-up Kiler (M4.13), which <see cref="User"/> locks into a user file when it is asked to.</summary>
    public AdminKey AdminKey { get; } = AdminKey.New();

    /// <summary>The check of <paramref name="level"/> in <see cref="Levels"/>.</summary>
    public string Check(int level) => Levels.Levels[level].Check!;

    /// <summary>
    /// A user file of <paramref name="account"/> with the keys of <paramref name="keyLevels"/> (by default 1 to
    /// <paramref name="level"/>) and the keys of <paramref name="own"/>, which are not the keys of their levels, locked
    /// together with <see cref="Password"/>, and with what <paramref name="adminKey"/> says of <see cref="AdminKey"/>, by
    /// default its public key, without which the sign-in is refused (M4.15); its rights sealed by <see cref="AdminKey"/> with
    /// <paramref name="seal"/>, as an admin action seals them (M4.14), else the session is a reader.
    /// </summary>
    public UserFile User(
        string account,
        int level,
        bool canEdit = false,
        bool isAdmin = false,
        bool mustChangePassword = false,
        int[]? keyLevels = null,
        IReadOnlyDictionary<int, SecretKey>? own = null,
        AdminKeyLock adminKey = AdminKeyLock.PublicKey,
        bool seal = false)
    {
        var keys = (keyLevels ?? Enumerable.Range(1, level).ToArray()).ToDictionary(key => key, key => _keys[key]);
        foreach (var (ownLevel, key) in own ?? new Dictionary<int, SecretKey>())
        {
            keys[ownLevel] = key;
        }

        var locked = adminKey == AdminKeyLock.None
            ? LevelKeys.Lock(keys, Password)
            : LevelKeys.Lock(keys, AdminKey.PublicKey, adminKey == AdminKeyLock.PublicAndPrivateKey ? AdminKey : null, Password);
        var user = new UserFile(account, $"Demo {account}", level, canEdit, isAdmin, mustChangePassword, locked);
        return seal ? user.Sealed(AdminKey) : user;
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

        AdminKey.Dispose();
    }
}

/// <summary>What of the admin key a user file of <see cref="TestKiler.User"/> has, locked with its level keys (M4.13).</summary>
internal enum AdminKeyLock
{
    /// <summary>Nothing, as a file that an admin action wrote before M4.14: the sign-in refuses it (M4.15).</summary>
    None,

    /// <summary>The public key, as a user's file.</summary>
    PublicKey,

    /// <summary>The public key and the private key, as an admin's file.</summary>
    PublicAndPrivateKey,
}

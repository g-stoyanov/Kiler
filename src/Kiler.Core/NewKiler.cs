using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Kiler.Crypto;
using Kiler.Storage;

namespace Kiler.Core;

/// <summary>
/// The documents of a new Kiler, which the wizard makes (M4.3, design 13, decided at the start of M4): <c>levels.json</c>, with
/// a new key and its check for each level from 1 and the recovery set, the keys locked with a new recovery key;
/// <c>settings.json</c>; the user file of the first admin, the current account, with the keys locked with their password; and
/// the four initial templates (M5.2, <see cref="InitialTemplates"/>). The level keys and the recovery key live only while it
/// is made (kiler-crypto, "Rules"): it keeps the locked documents, and the recovery key as it is shown, once, in the answer of
/// the wizard.
/// </summary>
public sealed class NewKiler
{
    private NewKiler(LevelsFile levels, SettingsFile settings, UserFile admin, IReadOnlyList<TemplateFile> templates, string recoveryKey)
    {
        Levels = levels;
        Settings = settings;
        Admin = admin;
        Templates = templates;
        RecoveryKey = recoveryKey;
    }

    public LevelsFile Levels { get; }

    public SettingsFile Settings { get; }

    /// <summary>The user file of the first admin: the top level, an editor and an admin, with the password that they chose, not a temporary one.</summary>
    public UserFile Admin { get; }

    /// <summary>The initial templates, with no level above the top level (<see cref="InitialTemplates.For"/>).</summary>
    public IReadOnlyList<TemplateFile> Templates { get; }

    /// <summary>
    /// The recovery key as it is shown (<see cref="Crypto.RecoveryKey.ToDisplayString"/>): for the answer of the wizard only,
    /// never a file, a line of the console or a later answer (kiler-crypto, "Passwords").
    /// </summary>
    public string RecoveryKey { get; }

    /// <summary>
    /// Whether the values of the wizard fit, before a key is made; <paramref name="invalid"/> says why not, for the page. The
    /// names are those of the levels 0 to the top level, in order, as <c>levels.json</c> takes them, so a level left out is a
    /// name that is null. The top level is the admin's.
    /// </summary>
    public static bool TryCheck(
        [NotNullWhen(true)] IReadOnlyList<string>? levelNames,
        [NotNullWhen(true)] string? displayName,
        [NotNullWhen(true)] string? password,
        int autoLockMinutes,
        [NotNullWhen(false)] out string? invalid)
    {
        invalid = levelNames is null ? "levels is missing."
            : displayName is null ? "displayName is missing."
            : password is null ? "password is missing."
            : Invalid(levelNames, displayName, password, autoLockMinutes);
        return invalid is null;
    }

    /// <summary>
    /// Makes the documents: a new key and its check for each level from 1, a new recovery key, and the keys locked with it
    /// and with <paramref name="password"/>. The keys are disposed before it returns. PBKDF2 runs twice, so it takes a moment.
    /// <paramref name="account"/> is the current account (<see cref="ICurrentAccount.Name"/>), the id of the admin's user file.
    /// </summary>
    /// <exception cref="ArgumentException">A value does not fit (<see cref="TryCheck"/>), or the account does not pass <see cref="StorageName"/>.</exception>
    public static NewKiler Make(string account, IReadOnlyList<string> levelNames, string displayName, ReadOnlySpan<char> password, bool guestMode, int autoLockMinutes)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(levelNames);
        ArgumentNullException.ThrowIfNull(displayName);
        StorageName.Check(account);
        if (Invalid(levelNames, displayName, password, autoLockMinutes) is { } reason)
        {
            throw new ArgumentException(reason);
        }

        var top = levelNames.Count - 1;
        var keys = new Dictionary<int, SecretKey>(top);
        try
        {
            var entries = new List<LevelEntry>(levelNames.Count) { new(0, levelNames[0], null) };
            for (var level = 1; level <= top; level++)
            {
                var key = SecretKey.New();
                keys.Add(level, key);
                entries.Add(new(level, levelNames[level], LevelCheck.Create(key, level)));
            }

            using var recoveryKey = Crypto.RecoveryKey.New();
            var levels = new LevelsFile(entries, LevelKeys.Lock(keys, recoveryKey));
            var admin = new UserFile(account, displayName, top, canEdit: true, isAdmin: true, mustChangePassword: false, LevelKeys.Lock(keys, password));
            return new(levels, new SettingsFile(guestMode, autoLockMinutes), admin, InitialTemplates.For(top), recoveryKey.ToDisplayString());
        }
        finally
        {
            foreach (var key in keys.Values)
            {
                key.Dispose();
            }
        }
    }

    /// <summary>
    /// Writes the documents through <paramref name="copy"/> (core.md, "Calling the layer"): <c>levels.json</c> first, as a
    /// create, so of two wizards at once the second meets it and writes nothing (decided at the start of M4); then
    /// <c>settings.json</c>, the templates and, last, the user file of the admin, so a Kiler that its admin signs in to has
    /// every document. It stops at the first document that is not written, and repeats none. Whether the data folder may
    /// become a new Kiler is the caller's check (<see cref="DataFolder.Create"/>).
    /// </summary>
    public NewKilerWrite Write(DataCopy copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        if (Create(copy, LevelsFile.Key, Levels.ToContent(), "levels.json", out var outcome) is { } notLevels)
        {
            return new(
                outcome switch
                {
                    WriteOutcome.Exists => NewKilerOutcome.Exists,
                    WriteOutcome.Offline => NewKilerOutcome.Offline,
                    _ => NewKilerOutcome.NotWritten,
                },
                notLevels);
        }

        var failure = Create(copy, SettingsFile.Key, Settings.ToContent(), "settings.json", out _);
        foreach (var template in Templates)
        {
            failure ??= Create(copy, template.Key, template.ToContent(), $"The template {template.Id}", out _);
        }

        // The admin last, so a Kiler that someone signs in to is whole; nothing writes a template until the exam (code review).
        failure ??= Create(copy, Admin.Key, Admin.ToContent(), "The user file of the admin", out _);
        return failure is null ? new(NewKilerOutcome.Made, null) : new(NewKilerOutcome.Partial, failure);
    }

    private static string? Invalid(IReadOnlyList<string> levelNames, string displayName, ReadOnlySpan<char> password, int autoLockMinutes)
    {
        // Each name is a level of levels.json, with its check made later; one that is null is left out.
        var levels = levelNames.Select((name, level) => name is null ? null : new LevelEntry(level, name, null)).ToList();
        return LevelsFile.Invalid(levels)
            ?? UserFile.Invalid(displayName, levels.Count - 1)
            ?? Password.Invalid(password)
            ?? SettingsFile.Invalid(autoLockMinutes);
    }

    /// <summary>Null when the document is written; else what was not written and why, for the console.</summary>
    private static string? Create(DataCopy copy, DocumentKey key, JsonObject content, string name, out WriteOutcome? outcome)
    {
        try
        {
            outcome = copy.Create(key, content).Outcome;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A share that cannot be reached before a load has said so (core.md, "Calling the layer").
            outcome = null;
            return $"{name} was not written: {ex.Message}";
        }

        return outcome switch
        {
            WriteOutcome.Written => null,
            WriteOutcome.Exists => $"{name} was not written, as it is there already.",
            WriteOutcome.Offline => $"{name} was not written, as the data folder cannot be reached.",
            _ => $"{name} was not written: {outcome}.",
        };
    }
}

/// <summary>How the writes of the wizard ended (<see cref="NewKiler.Write"/>).</summary>
public enum NewKilerOutcome
{
    /// <summary>Every document is written.</summary>
    Made,

    /// <summary><c>levels.json</c> is there: another wizard was first. Nothing was written.</summary>
    Exists,

    /// <summary>The copy is offline (<see cref="WriteOutcome.Offline"/>), so nothing was written.</summary>
    Offline,

    /// <summary><c>levels.json</c> could not be written, as the share did not answer; nothing was written.</summary>
    NotWritten,

    /// <summary>
    /// <c>levels.json</c> was written, but <c>settings.json</c>, the user file of the admin or a template was not, so the Kiler
    /// has no admin, no settings or not every template. Nothing removes it: the data folder is deleted, and the wizard runs again.
    /// </summary>
    Partial,
}

/// <param name="Outcome">How the writes ended.</param>
/// <param name="Failure">What was not written and why, for the console; null when the Kiler was made. It holds no secret.</param>
public sealed record NewKilerWrite(NewKilerOutcome Outcome, string? Failure);

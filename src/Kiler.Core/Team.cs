using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Kiler.Storage;

namespace Kiler.Core;

/// <summary>
/// The levels, the settings and the users of the local copy, in memory (M4.2): made when the copy opens, it follows each load
/// of the watch (<see cref="Follow"/>) and reads the copy again after a load that failed or a change of offline
/// (<see cref="Reload"/>, core.md, "Calling the layer"). What it has is one <see cref="TeamFiles"/>, never changed, which
/// each update replaces whole, so a request reads it without a lock and finds the files of one moment. The updates come on
/// the thread of the watch; they keep a lock of their own, so one from a request too (M4.3) never interleaves with them.
/// </summary>
public sealed class Team
{
    private readonly DataCopy _copy;
    private readonly Lock _gate = new();
    private volatile TeamFiles _files;

    /// <summary>Reads the team from the documents of <paramref name="copy"/>, as the last load left them.</summary>
    public Team(DataCopy copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        _copy = copy;
        _files = Read(copy.Documents());
    }

    /// <summary>What the team has now.</summary>
    public TeamFiles Files => _files;

    /// <summary>Takes what a load changed of the team: a file new, changed, damaged or gone. A load that changed none of them leaves <see cref="Files"/> as it is.</summary>
    public void Follow(LoadResult load)
    {
        ArgumentNullException.ThrowIfNull(load);
        lock (_gate)
        {
            var files = _files;
            var levels = files.Levels;
            var settings = files.Settings;
            var users = files.Users;
            foreach (var key in load.Gone)
            {
                if (key == LevelsFile.Key)
                {
                    levels = null;
                }
                else if (key == SettingsFile.Key)
                {
                    settings = null;
                }
                else if (IsUser(key))
                {
                    users = users.Remove(key.Id);
                }
            }

            foreach (var loaded in load.Changed)
            {
                Take(loaded, ref levels, ref settings, ref users);
            }

            if (levels != files.Levels || settings != files.Settings || users != files.Users)
            {
                _files = new TeamFiles(levels, settings, users);
            }
        }
    }

    /// <summary>
    /// Reads the team again from the documents of the copy: after <see cref="DataWatch.Failed"/> and at each
    /// <see cref="DataWatch.OfflineChanged"/>, as no later load gives what the load took (core.md). It reads every document of
    /// the copy, so it is not for each load.
    /// </summary>
    public void Reload()
    {
        lock (_gate)
        {
            _files = Read(_copy.Documents());
        }
    }

    private static TeamFiles Read(IReadOnlyList<LoadedDocument> documents)
    {
        TeamFile<LevelsFile>? levels = null;
        TeamFile<SettingsFile>? settings = null;
        var users = TeamFiles.Empty.Users;
        foreach (var loaded in documents)
        {
            Take(loaded, ref levels, ref settings, ref users);
        }

        return new TeamFiles(levels, settings, users);
    }

    private static void Take(
        LoadedDocument loaded,
        ref TeamFile<LevelsFile>? levels,
        ref TeamFile<SettingsFile>? settings,
        ref ImmutableSortedDictionary<string, TeamFile<UserFile>> users)
    {
        var key = loaded.Key;
        if (key == LevelsFile.Key)
        {
            levels = TeamFile<LevelsFile>.Of(loaded.Read, LevelsFile.TryRead);
        }
        else if (key == SettingsFile.Key)
        {
            settings = TeamFile<SettingsFile>.Of(loaded.Read, SettingsFile.TryRead);
        }
        else if (IsUser(key))
        {
            users = users.SetItem(key.Id, TeamFile<UserFile>.Of(loaded.Read, UserFile.TryRead));
        }
    }

    /// <summary>A document of <c>users\</c> of the data folder, not of a private folder.</summary>
    private static bool IsUser(DocumentKey key) => key.Account is null && key.Folder == DataFolder.Users;
}

/// <summary>
/// The files of the team at one moment (<see cref="Team.Files"/>), never changed. A file that the copy does not have is
/// <c>null</c>, or not in <see cref="Users"/>.
/// </summary>
public sealed class TeamFiles
{
    internal TeamFiles(TeamFile<LevelsFile>? levels, TeamFile<SettingsFile>? settings, ImmutableSortedDictionary<string, TeamFile<UserFile>> users)
    {
        Levels = levels;
        Settings = settings;
        Users = users;
    }

    public static TeamFiles Empty { get; } = new(null, null, ImmutableSortedDictionary.Create<string, TeamFile<UserFile>>(StringComparer.Ordinal));

    /// <summary><c>levels.json</c>; <c>null</c> when the copy does not have it.</summary>
    public TeamFile<LevelsFile>? Levels { get; }

    /// <summary><c>settings.json</c>; <c>null</c> when the copy does not have it.</summary>
    public TeamFile<SettingsFile>? Settings { get; }

    /// <summary>The user files of <c>users\</c>, by account, in the order of the accounts.</summary>
    public ImmutableSortedDictionary<string, TeamFile<UserFile>> Users { get; }
}

/// <summary>
/// A document of the team as the local copy has it: its model and its version, or why it does not fit, as anyone can write
/// the share. The reason names a JSON field or the damage, never a value from the share (core.md, "Models").
/// </summary>
public sealed class TeamFile<T>
    where T : class
{
    private TeamFile(int? version, T? model, string? invalid)
    {
        Version = version;
        Model = model;
        Invalid = invalid;
    }

    internal delegate bool TryRead(Document document, [NotNullWhen(true)] out T? model, [NotNullWhen(false)] out string? invalid);

    /// <summary>The version of the document, for a <see cref="DataCopy.Replace"/>; <c>null</c> when the file is damaged.</summary>
    public int? Version { get; }

    /// <summary>The model; <c>null</c> when the document does not fit it.</summary>
    public T? Model { get; }

    /// <summary>Why the document does not fit its model, or why its file is damaged; <c>null</c> when it fits.</summary>
    public string? Invalid { get; }

    [MemberNotNullWhen(true, nameof(Model))]
    [MemberNotNullWhen(false, nameof(Invalid))]
    public bool IsValid => Model is not null;

    internal static TeamFile<T> Of(DocumentRead read, TryRead tryRead)
    {
        if (read.IsDamaged)
        {
            return new(null, null, Damaged(read.Damage.Value));
        }

        var document = read.Document;
        return tryRead(document, out var model, out var invalid)
            ? new(document.Version, model, null)
            : new(document.Version, null, invalid);
    }

    private static string Damaged(DocumentDamage damage) => damage switch
    {
        DocumentDamage.TooLarge => "The file is larger than a document can be.",
        DocumentDamage.NotJson => "The file is not valid JSON.",
        DocumentDamage.NotAnObject => "The file is not a JSON object.",
        DocumentDamage.WrongId => "The id of the file is missing or is not its name.",
        DocumentDamage.WrongVersion => "The version of the file is missing or is not a whole number from 1.",
        _ => "The file is damaged.",
    };
}

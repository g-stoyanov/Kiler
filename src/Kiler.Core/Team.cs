using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Kiler.Storage;

namespace Kiler.Core;

/// <summary>
/// The levels, the settings, the users and the templates (M5.2) of the local copy, in memory (M4.2): made when the copy opens, it follows each load
/// of the watch (<see cref="Follow"/>) and reads the copy again after a load that failed or a change of offline
/// (<see cref="Reload"/>, core.md, "Calling the layer"). What it has is one <see cref="TeamFiles"/>, never changed, which
/// each update replaces whole, so a request reads it without a lock and finds the files of one moment. The updates come on
/// the thread of the watch; they keep a lock of their own, so one from a request too (M4.3) never interleaves with them. A
/// write of this Kiler goes through the team (<see cref="Replace(UserFile, int)"/>, M4.5a; <see cref="Create"/> and
/// <see cref="Replace(LevelsFile, int)"/>, M4.6), as no load gives it. With <c>levels.json</c>, <c>settings.json</c> and each
/// template it keeps the versions of them that the copy keeps, from which <see cref="TeamFiles.SealedLevels"/>,
/// <see cref="TeamFiles.SealedSettings"/> and <see cref="TeamFiles.TemplatesFor"/> take the newest that an admin sealed
/// (M4.16a, M4.16b).
/// </summary>
public sealed class Team
{
    private readonly DataCopy _copy;
    private readonly Lock _gate = new();
    private volatile TeamFiles _files;

    /// <summary>
    /// The versions that this Kiler wrote (<see cref="TakeWritten"/>) since the team last followed a load, by key; kept under the
    /// lock. The watch raises a load after it has let go of the copy, so the next load that the team follows may have ended
    /// before the write: <see cref="Follow"/> takes no older version of these from it (core.md, "The team"). The loads after it
    /// started after the write, as the watch runs one load at a time and raises its events before the next.
    /// </summary>
    private readonly Dictionary<DocumentKey, int> _written = [];

    /// <summary>Reads the team from the documents of <paramref name="copy"/>, as the last load left them.</summary>
    public Team(DataCopy copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        _copy = copy;
        _files = Read(copy, TeamFiles.Empty);
    }

    /// <summary>What the team has now.</summary>
    public TeamFiles Files => _files;

    /// <summary>
    /// Takes what a load changed of the team: a file new, changed, damaged or gone. A load that changed none of them leaves
    /// <see cref="Files"/> as it is. The versions that the copy keeps of <c>levels.json</c>, <c>settings.json</c> and a
    /// template are read again only for a file that the load gave, took away or reported changed outside Kiler (M4.16a,
    /// M4.16b), and outside the lock, as the copy can wait for a write under way. The limit: a slot that a later load takes
    /// from the history of the share, as the caps of a load put it off (storage.md), is seen at the next change of the file,
    /// or at <see cref="Reload"/>.
    /// </summary>
    public void Follow(LoadResult load)
    {
        ArgumentNullException.ThrowIfNull(load);

        // The writes of this Kiler keep the kept versions as they are, so those of any later TeamFiles are as good.
        var before = _files;
        var keptLevels = Touches(load, LevelsFile.Key) ? Kept(_copy, LevelsFile.Key, LevelsFile.TryRead, before.KeptLevels) : (ImmutableArray<TeamFile<LevelsFile>>?)null;
        var keptSettings = Touches(load, SettingsFile.Key) ? Kept(_copy, SettingsFile.Key, SettingsFile.TryRead, before.KeptSettings) : (ImmutableArray<TeamFile<SettingsFile>>?)null;
        var keptTemplates = TouchedTemplates(load).ToDictionary(id => id, id => KeptTemplate(_copy, id, before.KeptTemplates), StringComparer.Ordinal);
        lock (_gate)
        {
            var files = _files;
            var levels = files.Levels;
            var settings = files.Settings;
            var users = files.Users;
            var templates = files.Templates;
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
                else if (IsTemplate(key))
                {
                    templates = templates.Remove(key.Id);
                }
            }

            foreach (var loaded in load.Changed)
            {
                // A load that ended before a write of this Kiler: its older version does not take the place of the write.
                if (_written.TryGetValue(loaded.Key, out var wrote) && loaded.Read.Document?.Version < wrote)
                {
                    continue;
                }

                Take(loaded, ref levels, ref settings, ref users, ref templates);
            }

            _written.Clear();
            if (levels != files.Levels || settings != files.Settings || users != files.Users || templates != files.Templates
                || keptLevels is not null || keptSettings is not null || keptTemplates.Count > 0)
            {
                _files = new TeamFiles(
                    levels,
                    settings,
                    users,
                    templates,
                    keptLevels ?? files.KeptLevels,
                    keptSettings ?? files.KeptSettings,
                    WithKept(files.KeptTemplates, keptTemplates));
            }
        }
    }

    /// <summary>
    /// Replaces the version <paramref name="version"/> of the user file through the copy (M4.5a) and, when it is written, takes
    /// <paramref name="user"/> into <see cref="Files"/> at its new version: the copy takes what this Kiler wrote as its own, so
    /// no load gives it (core.md, "The team"). A newer version of the file, which a load has brought in meanwhile, stays.
    /// The write runs without the lock of the team, as it can wait for a load under way.
    /// </summary>
    /// <exception cref="IOException">The share could not be reached before a load said so (core.md, "Calling the layer").</exception>
    /// <exception cref="UnauthorizedAccessException">The share refused the write before a load said so.</exception>
    public WriteResult Replace(UserFile user, int version)
    {
        ArgumentNullException.ThrowIfNull(user);
        return TakeWritten(user.Key, _copy.Replace(user.Key, version, user.ToContent()), (files, written) => WithUser(files, user, written));
    }

    /// <summary>Creates the user file of a new user through the copy (M4.6), and takes it into <see cref="Files"/>, as <see cref="Replace(UserFile, int)"/> does.</summary>
    /// <exception cref="IOException">The share could not be reached before a load said so (core.md, "Calling the layer").</exception>
    /// <exception cref="UnauthorizedAccessException">The share refused the write before a load said so.</exception>
    public WriteResult Create(UserFile user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return TakeWritten(user.Key, _copy.Create(user.Key, user.ToContent()), (files, written) => WithUser(files, user, written));
    }

    /// <summary>Replaces the version <paramref name="version"/> of <c>levels.json</c> through the copy (M4.6), as <see cref="Replace(UserFile, int)"/> does a user file.</summary>
    /// <exception cref="IOException">The share could not be reached before a load said so (core.md, "Calling the layer").</exception>
    /// <exception cref="UnauthorizedAccessException">The share refused the write before a load said so.</exception>
    public WriteResult Replace(LevelsFile levels, int version)
    {
        ArgumentNullException.ThrowIfNull(levels);
        return TakeWritten(
            LevelsFile.Key,
            _copy.Replace(LevelsFile.Key, version, levels.ToContent()),
            (files, written) => files.Levels?.Version > written
                ? null
                : new TeamFiles(TeamFile<LevelsFile>.Written(written, levels.AsRead(written)), files.Settings, files.Users, files.Templates, files.KeptLevels, files.KeptSettings, files.KeptTemplates));
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
            _files = Read(_copy, _files);
        }
    }

    /// <summary>
    /// Takes a write of this Kiler into <see cref="Files"/> under the lock, when it is written: <paramref name="put"/> gives the
    /// files with it at its new version, or <c>null</c> when a load has brought in a newer version meanwhile, which stays.
    /// </summary>
    private WriteResult TakeWritten(DocumentKey key, WriteResult written, Func<TeamFiles, int, TeamFiles?> put)
    {
        if (written.Outcome != WriteOutcome.Written)
        {
            return written;
        }

        lock (_gate)
        {
            if (put(_files, written.Version) is { } files)
            {
                _files = files;
            }

            _written[key] = written.Version;
        }

        return written;
    }

    private static TeamFiles? WithUser(TeamFiles files, UserFile user, int version) =>
        files.Users.TryGetValue(user.Account, out var had) && had.Version > version
            ? null
            : new TeamFiles(files.Levels, files.Settings, files.Users.SetItem(user.Account, TeamFile<UserFile>.Written(version, user)), files.Templates, files.KeptLevels, files.KeptSettings, files.KeptTemplates);

    /// <summary>
    /// Reads the team from the documents of <paramref name="copy"/>, with the versions that it keeps of each template that it
    /// has, that it reports gone outside Kiler (<see cref="DataCopy.ToRestore"/>), or that <paramref name="before"/> knew. The
    /// limit: the copy reports a document gone some 30 s after it went (<c>InPlaceEdits.SettleTime</c> of Kiler.Storage), so a new team
    /// in that time, at a start, finds a template gone from the share in no load, until the load that reports it outside
    /// Kiler, which <see cref="Follow"/> takes.
    /// </summary>
    private static TeamFiles Read(DataCopy copy, TeamFiles before)
    {
        TeamFile<LevelsFile>? levels = null;
        TeamFile<SettingsFile>? settings = null;
        var users = TeamFiles.Empty.Users;
        var templates = TeamFiles.Empty.Templates;
        foreach (var loaded in copy.Documents())
        {
            Take(loaded, ref levels, ref settings, ref users, ref templates);
        }

        // A template gone outside Kiler is not among the documents, but the copy keeps it apart and in its history.
        var gone = copy.ToRestore().Where(change => !change.InTrash && IsTemplate(change.Key)).Select(change => change.Key.Id);
        var keptTemplates = templates.Keys.Concat(gone).Concat(before.Templates.Keys).Concat(before.KeptTemplates.Keys).Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, id => KeptTemplate(copy, id, before.KeptTemplates), StringComparer.Ordinal);
        return new TeamFiles(
            levels,
            settings,
            users,
            templates,
            Kept<LevelsFile>(copy, LevelsFile.Key, LevelsFile.TryRead, []),
            Kept<SettingsFile>(copy, SettingsFile.Key, SettingsFile.TryRead, []),
            WithKept(TeamFiles.Empty.KeptTemplates, keptTemplates));
    }

    /// <summary>The versions of the template <paramref name="id"/> that the copy keeps and that fit, as <see cref="Kept"/> gives them (M4.16b).</summary>
    private static ImmutableArray<TeamFile<TemplateFile>> KeptTemplate(
        DataCopy copy,
        string id,
        ImmutableSortedDictionary<string, ImmutableArray<TeamFile<TemplateFile>>> before) =>
        Kept(copy, TemplateFile.KeyOf(id), TemplateFile.TryRead, before.TryGetValue(id, out var had) ? had : []);

    /// <summary><paramref name="kept"/> with the versions read again of <paramref name="read"/>, by id; a template without any leaves it.</summary>
    private static ImmutableSortedDictionary<string, ImmutableArray<TeamFile<TemplateFile>>> WithKept(
        ImmutableSortedDictionary<string, ImmutableArray<TeamFile<TemplateFile>>> kept,
        Dictionary<string, ImmutableArray<TeamFile<TemplateFile>>> read)
    {
        var builder = kept.ToBuilder();
        foreach (var (id, versions) in read)
        {
            if (versions.IsEmpty)
            {
                builder.Remove(id);
            }
            else
            {
                builder[id] = versions;
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>The ids of the templates that a load gave, took away, or reported changed outside Kiler in their folder.</summary>
    private static IEnumerable<string> TouchedTemplates(LoadResult load) =>
        load.Changed.Select(loaded => loaded.Key)
            .Concat(load.Gone)
            .Concat(load.Outside.Where(change => !change.InTrash).Select(change => change.Key))
            .Where(IsTemplate)
            .Select(key => key.Id)
            .Distinct(StringComparer.Ordinal);

    /// <summary>
    /// The versions of a document of the root that the copy keeps and that fit their model, the newest first (M4.16a): the old
    /// versions of its history (<see cref="DataCopy.History"/>) and the last good bytes that it keeps apart for a change outside
    /// Kiler (<see cref="DataCopy.KeptApart"/>), each that <paramref name="before"/> has as it is in its place (<see cref="KeepKnown"/>).
    /// The share is not touched.
    /// </summary>
    private static ImmutableArray<TeamFile<T>> Kept<T>(DataCopy copy, DocumentKey key, TeamFile<T>.TryRead tryRead, ImmutableArray<TeamFile<T>> before)
        where T : class, ISealedFile
    {
        var kept = copy.History(key).ToList();
        if (copy.KeptApart(key) is { } apart)
        {
            kept.Add(apart);
        }

        return KeepKnown(kept.Select(document => TeamFile<T>.Of(document, tryRead)).Where(file => file.IsValid), before);
    }

    /// <summary>
    /// The versions <paramref name="read"/> again, the newest first, each that <paramref name="before"/> has at the same version
    /// and with the same seal (<see cref="FileSeal.SameAs"/>) in its place, so its model keeps the answers of its seal checks
    /// and only a new version is checked again: anyone who can write the share can plant thousands of versions in its history,
    /// each checked once per key (M4.16a security review).
    /// </summary>
    internal static ImmutableArray<TeamFile<T>> KeepKnown<T>(IEnumerable<TeamFile<T>> read, ImmutableArray<TeamFile<T>> before)
        where T : class, ISealedFile
    {
        var known = before.ToLookup(file => file.Version);
        return [.. read
            .Select(file => known[file.Version].FirstOrDefault(old => old.Model?.Seal is { } seal && file.Model?.Seal is { } now && seal.SameAs(now)) ?? file)
            .OrderByDescending(file => file.Version)];
    }

    /// <summary>Whether a load gave <paramref name="key"/>, took it away, or reported it changed outside Kiler in its folder.</summary>
    private static bool Touches(LoadResult load, DocumentKey key) =>
        load.Changed.Any(loaded => loaded.Key == key) || load.Gone.Contains(key) || load.Outside.Any(change => change.Key == key && !change.InTrash);

    private static void Take(
        LoadedDocument loaded,
        ref TeamFile<LevelsFile>? levels,
        ref TeamFile<SettingsFile>? settings,
        ref ImmutableSortedDictionary<string, TeamFile<UserFile>> users,
        ref ImmutableSortedDictionary<string, TeamFile<TemplateFile>> templates)
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
        else if (IsTemplate(key))
        {
            templates = templates.SetItem(key.Id, TeamFile<TemplateFile>.Of(loaded.Read, TemplateFile.TryRead));
        }
    }

    /// <summary>A document of <c>users\</c> of the data folder, not of a private folder.</summary>
    private static bool IsUser(DocumentKey key) => key.Account is null && key.Folder == DataFolder.Users;

    /// <summary>A document of <c>templates\</c> of the data folder.</summary>
    private static bool IsTemplate(DocumentKey key) => key.Account is null && key.Folder == DataFolder.Templates;
}

/// <summary>
/// The files of the team at one moment (<see cref="Team.Files"/>), never changed. A file that the copy does not have is
/// <c>null</c>, or not in <see cref="Users"/>.
/// </summary>
public sealed class TeamFiles
{
    /// <summary>The newest sealed versions for the last public key that asked, as the automatic lock asks at each request.</summary>
    private volatile SealedBy? _sealed;

    internal TeamFiles(
        TeamFile<LevelsFile>? levels,
        TeamFile<SettingsFile>? settings,
        ImmutableSortedDictionary<string, TeamFile<UserFile>> users,
        ImmutableSortedDictionary<string, TeamFile<TemplateFile>> templates,
        ImmutableArray<TeamFile<LevelsFile>> keptLevels,
        ImmutableArray<TeamFile<SettingsFile>> keptSettings,
        ImmutableSortedDictionary<string, ImmutableArray<TeamFile<TemplateFile>>> keptTemplates)
    {
        Levels = levels;
        Settings = settings;
        Users = users;
        Templates = templates;
        KeptLevels = keptLevels;
        KeptSettings = keptSettings;
        KeptTemplates = keptTemplates;
    }

    public static TeamFiles Empty { get; } = new(
        null,
        null,
        ImmutableSortedDictionary.Create<string, TeamFile<UserFile>>(StringComparer.Ordinal),
        ImmutableSortedDictionary.Create<string, TeamFile<TemplateFile>>(StringComparer.Ordinal),
        [],
        [],
        ImmutableSortedDictionary.Create<string, ImmutableArray<TeamFile<TemplateFile>>>(StringComparer.Ordinal));

    /// <summary><c>levels.json</c>; <c>null</c> when the copy does not have it.</summary>
    public TeamFile<LevelsFile>? Levels { get; }

    /// <summary><c>settings.json</c>; <c>null</c> when the copy does not have it.</summary>
    public TeamFile<SettingsFile>? Settings { get; }

    /// <summary>The user files of <c>users\</c>, by account, in the order of the accounts.</summary>
    public ImmutableSortedDictionary<string, TeamFile<UserFile>> Users { get; }

    /// <summary>
    /// The templates of <c>templates\</c> (M5.2), by id, in the order of the ids, as they are on the share; a request of a
    /// session takes <see cref="TemplatesFor"/> (M4.16b). Read only until the exam (decided at the start of M5).
    /// </summary>
    public ImmutableSortedDictionary<string, TeamFile<TemplateFile>> Templates { get; }

    /// <summary>
    /// Whether a version of <c>levels.json</c> fits its model, on the share or in the copy (M4.16a), so a sign-in can check its
    /// seal: one damaged or gone on the share is taken from the copy.
    /// </summary>
    public bool HasLevels => Levels is { IsValid: true } || !KeptLevels.IsEmpty;

    /// <summary>The versions of <c>levels.json</c> that the copy keeps and that fit, the newest first (M4.16a).</summary>
    internal ImmutableArray<TeamFile<LevelsFile>> KeptLevels { get; }

    /// <summary>The versions of <c>settings.json</c> that the copy keeps and that fit, the newest first.</summary>
    internal ImmutableArray<TeamFile<SettingsFile>> KeptSettings { get; }

    /// <summary>
    /// The versions of each template that the copy keeps and that fit, the newest first, by id (M4.16b); a template gone from
    /// the share is here while the copy keeps it apart, and one without any kept version is not.
    /// </summary>
    internal ImmutableSortedDictionary<string, ImmutableArray<TeamFile<TemplateFile>>> KeptTemplates { get; }

    /// <summary>
    /// The newest version of <c>levels.json</c> that <paramref name="publicKey"/> verifies (<see cref="SealedFile{T}"/>), or
    /// <c>null</c> when neither the share nor the copy has one: the sign-in, the rights of an admin and the admin actions take
    /// it (decided by the user in M4.16).
    /// </summary>
    public SealedFile<LevelsFile>? SealedLevels(string publicKey) => SealedFor(publicKey).Levels;

    /// <summary>The newest version of <c>settings.json</c> that <paramref name="publicKey"/> verifies, or <c>null</c>: the automatic lock takes its minutes.</summary>
    public SealedFile<SettingsFile>? SealedSettings(string publicKey) => SealedFor(publicKey).Settings;

    /// <summary>
    /// The <c>levels.json</c> that a request of <paramref name="session"/> takes, for the names, the levels of a form and the
    /// top level of the audit: the newest that the public key of the session verifies (<see cref="SealedLevels"/>). A guest,
    /// a request without a session, has no key to check a seal with, so it takes the file on the share as it is, as guest mode
    /// follows <c>settings.json</c> as it is (limit (4) of the seal, M3.md): it reads level 0, which is in clear on the share.
    /// <c>null</c> when there is none.
    /// </summary>
    public LevelsFile? LevelsFor(Session? session) =>
        session is null ? Levels?.Model : session.AdminPublicKey is { } publicKey ? SealedLevels(publicKey)?.Model : null;

    /// <summary>
    /// The templates that a request of <paramref name="session"/> takes, by id (M4.16b, decided by the user in M4.16): for
    /// the records, the view of a record, <c>GET /api/templates</c> and the record actions. Each is the newest version that
    /// the public key of the session verifies, as <see cref="SealedLevels"/> takes it, so a template changed, damaged or gone
    /// on the share is read as an admin sealed it; one without such a version is the file on the share as it is, whose
    /// records are read but which takes no new record or edit (<see cref="TemplateFile.IsSealedFor"/>), and one gone from the
    /// share without it is not here. A guest has no key to check a seal with, so it takes <see cref="Templates"/>, as
    /// <see cref="LevelsFor"/> does, and so does a session without the public key, for which nothing is sealed. A template
    /// that is the same as before is the same <see cref="TeamFile{T}"/>, so <see cref="Records"/> reads again only the
    /// records of one that changed.
    /// </summary>
    public ImmutableSortedDictionary<string, TeamFile<TemplateFile>> TemplatesFor(Session? session) =>
        session?.AdminPublicKey is { } publicKey ? SealedFor(publicKey).Templates : Templates;

    /// <summary>
    /// The highest version that <paramref name="publicKey"/> verifies, among the file on the share and the versions that the
    /// copy keeps, the newest first; at the same version, the file on the share.
    /// </summary>
    private static SealedFile<T>? Newest<T>(TeamFile<T>? onShare, ImmutableArray<TeamFile<T>> kept, string publicKey)
        where T : class, ISealedFile =>
        NewestFile(onShare, kept, publicKey) is { } file ? new SealedFile<T>(file.Model!, file.Version!.Value, OnShare: ReferenceEquals(file, onShare)) : null;

    /// <summary>The <see cref="TeamFile{T}"/> of <see cref="Newest"/>: <paramref name="onShare"/> itself, or one of <paramref name="kept"/>.</summary>
    private static TeamFile<T>? NewestFile<T>(TeamFile<T>? onShare, ImmutableArray<TeamFile<T>> kept, string publicKey)
        where T : class, ISealedFile
    {
        // A file that fits its model is not damaged, so it has a version.
        var newest = onShare is { IsValid: true } && onShare.Model.IsSealedBy(publicKey) ? onShare : null;
        foreach (var file in kept)
        {
            if (file.Version <= newest?.Version)
            {
                break;
            }

            if (file.Model!.IsSealedBy(publicKey))
            {
                return file;
            }
        }

        return newest;
    }

    /// <summary>The templates of <see cref="TemplatesFor"/> for <paramref name="publicKey"/>; <see cref="Templates"/> itself when each is the file on the share.</summary>
    private ImmutableSortedDictionary<string, TeamFile<TemplateFile>> TemplatesBy(string publicKey)
    {
        var templates = Templates.ToBuilder();
        foreach (var (id, kept) in KeptTemplates)
        {
            var onShare = Templates.GetValueOrDefault(id);
            if (NewestFile(onShare, kept, publicKey) is { } newest && newest != onShare)
            {
                templates[id] = newest;
            }
        }

        return templates.ToImmutable();
    }

    private SealedBy SealedFor(string publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        if (_sealed is { } known && string.Equals(known.PublicKey, publicKey, StringComparison.Ordinal))
        {
            return known;
        }

        var made = new SealedBy(publicKey, Newest(Levels, KeptLevels, publicKey), Newest(Settings, KeptSettings, publicKey), TemplatesBy(publicKey));
        _sealed = made;
        return made;
    }

    private sealed record SealedBy(
        string PublicKey,
        SealedFile<LevelsFile>? Levels,
        SealedFile<SettingsFile>? Settings,
        ImmutableSortedDictionary<string, TeamFile<TemplateFile>> Templates);
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

    /// <summary>A model that this Kiler wrote at <paramref name="version"/> (<see cref="Team.Replace(UserFile, int)"/>).</summary>
    internal static TeamFile<T> Written(int version, T model) => new(version, model, null);

    internal static TeamFile<T> Of(DocumentRead read, TryRead tryRead)
    {
        if (read.IsDamaged)
        {
            return new(null, null, Damaged(read.Damage.Value));
        }

        return Of(read.Document, tryRead);
    }

    /// <summary>The model of a document that is not damaged, such as the one on disk at a conflict (M5.8).</summary>
    internal static TeamFile<T> Of(Document document, TryRead tryRead) =>
        tryRead(document, out var model, out var invalid)
            ? new(document.Version, model, null)
            : new(document.Version, null, invalid);

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

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Kiler.Crypto;
using Kiler.Storage;

namespace Kiler.Core;

/// <summary>
/// A level of <c>levels.json</c> (design 7.1): its number, its name and, from level 1, the check value of its key
/// (<see cref="LevelCheck"/>). A check that is missing is <c>null</c> and counts as not verified (M4.4); level 0 has no key,
/// so it has none.
/// </summary>
public sealed record LevelEntry(int Level, string Name, string? Check);

/// <summary>
/// <c>levels.json</c> (decided at the start of M4): the levels 0 to <see cref="Top"/>, without gaps, and the recovery set,
/// the level keys locked with the recovery key (M3.3). <c>id</c> and <c>version</c> are the document's. It is on the share,
/// so a content that does not fit is invalid, with a reason (<see cref="TryRead(Document, out LevelsFile?, out string?)"/>).
/// </summary>
public sealed class LevelsFile
{
    /// <summary>The highest top level, so the levels are 0 to 9 at most.</summary>
    public const int MaxTop = 9;

    public const int MaxNameLength = 40;

    private const string RecoveryPath = "recovery.";

    /// <param name="levels">The levels 0 to the top level, in order.</param>
    /// <param name="recovery">The keys of the levels 1 to the top level, locked with the recovery key.</param>
    /// <exception cref="ArgumentException">The levels or the recovery set do not fit; the message says why.</exception>
    public LevelsFile(IReadOnlyList<LevelEntry> levels, LockedKeys recovery)
    {
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(recovery);
        if ((Invalid(levels) ?? KeySets.Invalid(recovery, levels.Count - 1, RecoveryPath)) is { } reason)
        {
            throw new ArgumentException(reason, nameof(levels));
        }

        Levels = [.. levels];
        Recovery = KeySets.Copy(recovery);
    }

    public static DocumentKey Key { get; } = DocumentKey.AtRoot("levels");

    /// <summary>The levels 0 to <see cref="Top"/>; the index of each is its number.</summary>
    public IReadOnlyList<LevelEntry> Levels { get; }

    public int Top => Levels.Count - 1;

    public LockedKeys Recovery { get; }

    /// <summary>The model of the document, or why its content does not fit.</summary>
    /// <exception cref="ArgumentException">The document is not <see cref="Key"/>.</exception>
    public static bool TryRead(Document document, [NotNullWhen(true)] out LevelsFile? levels, [NotNullWhen(false)] out string? invalid)
    {
        ArgumentNullException.ThrowIfNull(document);
        return TryRead(document.Key, document.Content, out levels, out invalid);
    }

    /// <summary>The content of the document, without <c>id</c> and <c>version</c>, which the layer adds.</summary>
    public JsonObject ToContent() => ModelJson.Write(
        new LevelsJson
        {
            Levels = [.. Levels.Select(entry => new LevelJson { Level = entry.Level, Name = entry.Name, Check = entry.Check })],
            Recovery = KeySets.Write(Recovery),
        },
        CoreJsonContext.Default.LevelsJson);

    internal static bool TryRead(DocumentKey key, JsonObject content, [NotNullWhen(true)] out LevelsFile? levels, [NotNullWhen(false)] out string? invalid)
    {
        if (key != Key)
        {
            throw new ArgumentException("The document is not levels.json.", nameof(key));
        }

        levels = null;
        if (ModelJson.Read(content, CoreJsonContext.Default.LevelsJson) is not { } json)
        {
            invalid = ModelJson.WrongType;
            return false;
        }

        if (json.Levels is null)
        {
            invalid = "levels is missing.";
            return false;
        }

        var entries = new List<LevelEntry>(json.Levels.Count);
        for (var i = 0; i < json.Levels.Count; i++)
        {
            if (json.Levels[i] is not { } level)
            {
                invalid = $"Entry {i} of levels is null.";
                return false;
            }

            if (level.Level is not { } number)
            {
                invalid = $"Entry {i} of levels has no level.";
                return false;
            }

            if (level.Name is not { } name)
            {
                invalid = $"Entry {i} of levels has no name.";
                return false;
            }

            entries.Add(new LevelEntry(number, name, level.Check));
        }

        invalid = Invalid(entries);
        if (invalid is not null)
        {
            return false;
        }

        if (json.Recovery is not { } set)
        {
            invalid = "recovery is missing.";
            return false;
        }

        if (!KeySets.TryRead(set.Salt, set.Keys, entries.Count - 1, RecoveryPath, out var recovery, out invalid))
        {
            return false;
        }

        levels = new LevelsFile(entries, recovery);
        return true;
    }

    /// <summary>Why the levels do not fit, or <c>null</c>.</summary>
    private static string? Invalid(IReadOnlyList<LevelEntry?> levels)
    {
        if (levels.Count is < 2 or > MaxTop + 1)
        {
            return $"The levels are not 0 to a top level from 1 to {MaxTop}.";
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < levels.Count; i++)
        {
            if (levels[i] is not { } level)
            {
                return $"Entry {i} of levels is null.";
            }

            if (level.Level != i)
            {
                return "The levels are not 0, 1, 2 and so on, each once and in order.";
            }

            if (level.Name is null || level.Name.Length > MaxNameLength || string.IsNullOrWhiteSpace(level.Name))
            {
                return $"The name of level {i} is blank or longer than {MaxNameLength} characters.";
            }

            if (!names.Add(level.Name))
            {
                return $"Level {i} has the name of a level below it, without regard to case.";
            }

            if (i == 0 && level.Check is not null)
            {
                return "Level 0 has a check, though it has no key.";
            }
        }

        return null;
    }
}

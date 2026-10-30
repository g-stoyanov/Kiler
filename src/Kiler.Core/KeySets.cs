using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Kiler.Crypto;

namespace Kiler.Core;

/// <summary>
/// A locked set of level keys in a document (M3.3): the <c>salt</c> of a user file or of the recovery set, and its
/// <c>keys</c>, each under the digit of its level, <c>"1"</c> to <c>"9"</c>. A name that is not the digit of a level makes the
/// document invalid (decided by the user in M4.1); the values are checked by <see cref="LevelKeys.Unlock(LockedKeys, ReadOnlySpan{char})"/>.
/// </summary>
internal static class KeySets
{
    /// <summary>The set from its JSON, or why it does not fit; <paramref name="path"/> is where it is, before its fields, in the reason.</summary>
    public static bool TryRead(
        string? salt,
        Dictionary<string, string?>? keys,
        int top,
        string path,
        [NotNullWhen(true)] out LockedKeys? locked,
        [NotNullWhen(false)] out string? invalid)
    {
        locked = null;
        if (salt is null)
        {
            invalid = $"{path}salt is missing.";
            return false;
        }

        if (keys is null)
        {
            invalid = $"{path}keys is missing.";
            return false;
        }

        var levels = new Dictionary<int, string>(keys.Count);
        foreach (var (name, value) in keys)
        {
            if (LevelOf(name, top) is not { } level)
            {
                invalid = $"{path}keys has a name that is not the digit of a level from 1 to {top}.";
                return false;
            }

            if (value is null)
            {
                invalid = $"{path}keys has no value for level {level}.";
                return false;
            }

            levels.Add(level, value);
        }

        locked = new LockedKeys(salt, levels);
        invalid = null;
        return true;
    }

    /// <summary>Why a set given to a model does not fit, or <c>null</c>.</summary>
    public static string? Invalid(LockedKeys locked, int top, string path)
    {
        foreach (var (level, value) in locked.Keys)
        {
            if (level < 1 || level > top)
            {
                return $"{path}keys has a level that is not from 1 to {top}.";
            }

            if (value is null)
            {
                return $"{path}keys has no value for level {level}.";
            }
        }

        return null;
    }

    /// <summary>A copy of the set, so a model does not change with the dictionary it was given.</summary>
    public static LockedKeys Copy(LockedKeys locked) => new(locked.Salt, new Dictionary<int, string>(locked.Keys));

    /// <summary>The JSON of the set, its keys in the order of the levels.</summary>
    public static KeysJson Write(LockedKeys locked) => new()
    {
        Salt = locked.Salt,
        Keys = locked.Keys.OrderBy(key => key.Key).ToDictionary(key => key.Key.ToString(CultureInfo.InvariantCulture), key => (string?)key.Value),
    };

    /// <summary>The level of a name that is its digit, from 1 to <paramref name="top"/>: not <c>"01"</c>, <c>"+1"</c> or <c>" 1"</c>.</summary>
    internal static int? LevelOf(string name, int top) => name is [var digit] && digit >= '1' && digit <= '0' + top ? digit - '0' : null;
}

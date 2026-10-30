using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Kiler.Core;

/// <summary>
/// The id of a record or a stash (decided at the start of M5): 8 characters of <see cref="Password.TemporaryAlphabet"/>,
/// 40 random bits, such as <c>k3x9q2mf</c>. Each one passes <see cref="Kiler.Storage.StorageName"/>, so a key can be built
/// from it; a file of another name is not a record or a stash.
/// </summary>
public static class ItemId
{
    public const int Length = 8;

    /// <summary>Whether <paramref name="id"/> is an id of a record or a stash.</summary>
    public static bool IsValid([NotNullWhen(true)] string? id) =>
        id is { Length: Length } && !id.AsSpan().ContainsAnyExcept(Password.TemporaryAlphabet);

    /// <summary>A new random id (M5.4). A create that meets <see cref="Kiler.Storage.WriteOutcome.Exists"/> takes another one (core.md, "Calling the layer").</summary>
    public static string New() => RandomNumberGenerator.GetString(Password.TemporaryAlphabet, Length);
}

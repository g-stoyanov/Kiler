using System.Globalization;
using System.Runtime.CompilerServices;

namespace Kiler.Crypto;

/// <summary>A level as a part of the context of an encrypted value (M3.2). Level 0 is not encrypted, so it has neither a key nor a context.</summary>
internal static class Level
{
    public const int Lowest = 1;

    /// <summary>The level as its digits, as every value of a level carries it in its associated data.</summary>
    public static string Context(int level, [CallerArgumentExpression(nameof(level))] string? name = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, Lowest, name);
        return level.ToString(CultureInfo.InvariantCulture);
    }
}

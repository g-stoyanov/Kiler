using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Kiler.Crypto;

/// <summary>
/// The version of a release, <c>major.minor.patch</c> (M3.4). Its text is signed with the hash of the exe
/// (<see cref="UpdateSignature"/>), so a version has one text: decimal ASCII digits without a sign, white space or a leading
/// zero, each part at most <see cref="int.MaxValue"/>. Versions compare by number, the major part first, so 1.10.0 is higher
/// than 1.9.0; a local copy takes only a version higher than its own (decided on 2026-11-11).
/// </summary>
public readonly struct ReleaseVersion : IEquatable<ReleaseVersion>, IComparable<ReleaseVersion>
{
    public ReleaseVersion(int major, int minor, int patch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);
        Major = major;
        Minor = minor;
        Patch = patch;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>The version of <paramref name="text"/> in its one form; any other text, such as <c>1.0.0+abc</c>, is false.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out ReleaseVersion version)
    {
        version = default;
        if (text is null)
        {
            return false;
        }

        Span<Range> parts = stackalloc Range[4];
        var span = text.AsSpan();
        if (span.Split(parts, '.') != 3
            || !TryParsePart(span[parts[0]], out var major)
            || !TryParsePart(span[parts[1]], out var minor)
            || !TryParsePart(span[parts[2]], out var patch))
        {
            return false;
        }

        version = new ReleaseVersion(major, minor, patch);
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public bool Equals(ReleaseVersion other) => Major == other.Major && Minor == other.Minor && Patch == other.Patch;

    public override bool Equals(object? obj) => obj is ReleaseVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch);

    /// <summary>The one text of the version, which is signed.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    public static bool operator ==(ReleaseVersion left, ReleaseVersion right) => left.Equals(right);

    public static bool operator !=(ReleaseVersion left, ReleaseVersion right) => !left.Equals(right);

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    private static bool TryParsePart(ReadOnlySpan<char> part, out int value)
    {
        value = 0;

        // int.TryParse with NumberStyles.None takes only ASCII digits; a leading zero would give a second text of the number.
        return part.Length > 0
            && (part.Length == 1 || part[0] != '0')
            && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}

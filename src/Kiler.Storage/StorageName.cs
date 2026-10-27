using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Kiler.Storage;

/// <summary>
/// The check of a name that becomes part of a path: a document id, an account, a folder (M2.1). Names come from the shared
/// folder, which anyone can write, and become paths there and in the local copy in the profile, so every name is checked
/// before a path is built from it. Only lowercase ASCII letters, digits, <c>.</c>, <c>-</c> and <c>_</c>: Windows does not
/// tell case apart and a NAS may, so two names that differ only in case could be two files on the share and one in the
/// local copy.
/// </summary>
public static class StorageName
{
    /// <summary>The longest name. A file name adds <c>.json</c> to it.</summary>
    public const int MaxLength = 64;

    /// <summary>The ending of the layer's temporary files and folders, which no name has, so a leftover one is never taken for a document or an account.</summary>
    internal const string TemporaryEnding = ".tmp";

    private static readonly SearchValues<char> Allowed = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789.-_");

    public static bool IsValid([NotNullWhen(true)] string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxLength || name.AsSpan().ContainsAnyExcept(Allowed))
        {
            return false;
        }

        // Windows drops a trailing dot, so "a." would be the file "a"; a leading one and ".." are never a Kiler name.
        if (name[0] == '.' || name[^1] == '.' || name.Contains("..", StringComparison.Ordinal) || name.EndsWith(TemporaryEnding, StringComparison.Ordinal))
        {
            return false;
        }

        // Windows takes a device name as the device, also with an extension: "nul.json" is NUL.
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        return !IsDeviceName(dot < 0 ? name : name.AsSpan(0, dot));
    }

    /// <summary>
    /// Gives the name back when it passes the check, else an <see cref="ArgumentException"/>. The message does not repeat
    /// the name, which can hold anything, control characters too.
    /// </summary>
    public static string Check(string name, [CallerArgumentExpression(nameof(name))] string? paramName = null) =>
        IsValid(name) ? name : throw new ArgumentException("Not a valid name for a file or folder of the data layer.", paramName);

    /// <summary>
    /// A raw name from the shared folder in a form that can be reported and shown (M2.7): printable ASCII as it is, a
    /// backslash doubled, and every other UTF-16 unit, control characters, bidi marks and the halves of a surrogate pair too,
    /// as a backslash, <c>u</c> and its code in four hex digits. A name on the share can have up to 255 units, so only the
    /// first <see cref="MaxLength"/> are shown, followed by <c>...</c> when there are more. Anyone can name a file on the share,
    /// so a report never carries the raw name.
    /// </summary>
    public static string Escape(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var shown = raw.AsSpan(0, Math.Min(raw.Length, MaxLength));
        var escaped = new StringBuilder(shown.Length + 3);
        foreach (var unit in shown)
        {
            if (unit == '\\')
            {
                escaped.Append(@"\\");
            }
            else if (unit is >= ' ' and <= '~')
            {
                escaped.Append(unit);
            }
            else
            {
                escaped.Append(CultureInfo.InvariantCulture, $@"\u{(int)unit:x4}");
            }
        }

        if (raw.Length > MaxLength)
        {
            escaped.Append("...");
        }

        return escaped.ToString();
    }

    /// <summary>CON, PRN, AUX, NUL, COM0 to COM9 and LPT0 to LPT9, in lowercase, as the check lets nothing else through.</summary>
    private static bool IsDeviceName(ReadOnlySpan<char> stem) =>
        stem is "con" or "prn" or "aux" or "nul"
        || (stem.Length == 4 && (stem.StartsWith("com") || stem.StartsWith("lpt")) && char.IsAsciiDigit(stem[3]));
}

using System.Diagnostics.CodeAnalysis;

namespace Kiler.Crypto;

/// <summary>
/// Base64 of the standard alphabet with padding, read only in its one form (M3.1). <see cref="Convert"/> takes white space
/// and bits set after the last byte, so the same bytes have more than one text: only the text that
/// <see cref="Convert.ToBase64String(byte[])"/> writes is read.
/// </summary>
internal static class CanonicalBase64
{
    public static bool TryDecode(string text, [NotNullWhen(true)] out byte[]? bytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            bytes = Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            bytes = null;
            return false;
        }

        if (!string.Equals(Convert.ToBase64String(bytes), text, StringComparison.Ordinal))
        {
            bytes = null;
            return false;
        }

        return true;
    }
}

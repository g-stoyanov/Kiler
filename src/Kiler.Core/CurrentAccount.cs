using System.Buffers;
using System.Text;
using Kiler.Storage;

namespace Kiler.Core;

/// <summary>
/// The account that Kiler runs for (decided at the start of M4): the Windows user name without the domain, in lowercase, or
/// the account of <c>--account</c>. It is the id of <c>users\&lt;account&gt;.json</c>. Kiler.App implements it; the tests use
/// a fake.
/// </summary>
public interface ICurrentAccount
{
    /// <summary>
    /// The account (<see cref="AccountName.From"/>); <c>null</c> when the Windows user name cannot be one, so the user cannot
    /// sign in, and the screen says so.
    /// </summary>
    string? Name { get; }
}

public static class AccountName
{
    /// <summary>
    /// The account of a Windows user name: the part after the domain (<c>DOMAIN\user</c>), with the ASCII letters in
    /// lowercase; <c>null</c> when it does not pass <see cref="StorageName"/>. A letter outside ASCII is not made lowercase,
    /// so it fails the check instead of becoming an ASCII one, as the Kelvin sign would become <c>k</c>.
    /// </summary>
    public static string? From(string windowsName)
    {
        ArgumentNullException.ThrowIfNull(windowsName);
        var name = windowsName.AsSpan(windowsName.LastIndexOf('\\') + 1);
        if (name.Length is 0 or > StorageName.MaxLength)
        {
            return null;
        }

        Span<char> lower = stackalloc char[name.Length];
        if (Ascii.ToLower(name, lower, out _) != OperationStatus.Done)
        {
            return null;
        }

        var account = new string(lower);
        return StorageName.IsValid(account) ? account : null;
    }
}

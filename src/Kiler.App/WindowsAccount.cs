using System.Security.Principal;
using Kiler.Core;

namespace Kiler.App;

/// <summary>
/// The account of this Kiler (decided at the start of M4): the one of <c>--account</c> (<see cref="StartOptions.Account"/>),
/// else the user of the token of the process, <c>DOMAIN\user</c>, as <see cref="AccountName.From"/> makes it. Not the
/// <c>USERNAME</c> variable, which a start can set to anything.
/// </summary>
internal sealed class WindowsAccount(string? given) : ICurrentAccount
{
    public string? Name { get; } = given ?? OfTheProcess();

    private static string? OfTheProcess()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return AccountName.From(identity.Name);
    }
}

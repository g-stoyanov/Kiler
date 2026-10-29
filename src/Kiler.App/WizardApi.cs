using Kiler.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kiler.App;

/// <summary>What the wizard sends (M4.3).</summary>
/// <param name="Levels">The names of the levels 0 to the top level, in order; the top level is the admin's.</param>
/// <param name="DisplayName">The display name of the first admin, the current account.</param>
/// <param name="Password">The admin's password; the page asks for it twice and checks that the two match.</param>
/// <param name="GuestMode">Whether Kiler opens as a guest, who reads level 0, before a sign-in.</param>
/// <param name="AutoLockMinutes">The minutes without activity after which the session locks.</param>
internal sealed record WizardRequest(string[]? Levels, string? DisplayName, string? Password, bool GuestMode, int AutoLockMinutes)
{
    /// <summary>The name of the type: a record prints its properties, and this one holds a password (SKILL.md, "Style").</summary>
    public override string ToString() => nameof(WizardRequest);
}

/// <param name="RecoveryKey">The recovery key, which only this answer gives (kiler-crypto, "Passwords").</param>
internal sealed record WizardResponse(string RecoveryKey)
{
    /// <summary>The name of the type, as it holds the recovery key.</summary>
    public override string ToString() => nameof(WizardResponse);
}

/// <param name="Error">Why the request was refused, for the page.</param>
internal sealed record ErrorResponse(string Error);

/// <summary>
/// <c>POST /api/wizard</c> (M4.3, step 2 of M4): makes a new Kiler in the data folder of the start, with the current account as
/// its first admin (<see cref="KilerData.TryMakeNew"/>), and answers with the recovery key, once; the admin then signs in
/// (M4.4). It needs no session, as there is no user before it, and runs only where a data folder may become a new Kiler.
/// Values that do not fit are 400; a wizard that cannot run, or made no Kiler, is 409, and the page reads the state of the
/// data again (<see cref="DataApi"/>).
/// </summary>
internal static class WizardApi
{
    public const string NoAccount = "The name of this Windows account cannot be the name of a Kiler account, so it cannot sign in.";

    public static void Map(IEndpointRouteBuilder endpoints, KilerData data, ICurrentAccount account) =>
        endpoints.MapPost("/api/wizard", (WizardRequest request) => Run(data, account, request));

    /// <summary>What the page says when the wizard made no Kiler.</summary>
    public static string Message(WizardRefusal refusal) => refusal switch
    {
        WizardRefusal.NoFolder => "No data folder has been chosen: start Kiler from the shared folder, or give it the data folder with --data.",
        WizardRefusal.Opening => "The data folder is opening. Try again in a moment.",
        WizardRefusal.Offline => "The shared folder cannot be reached. The new Kiler can be made once it is back.",
        WizardRefusal.NotNew => "The data folder, or the local copy of Kiler, has had documents, or the data folder cannot be written, so it does not become a new Kiler. The console of Kiler says which.",
        WizardRefusal.Exists => "Another Kiler has just been made in this data folder.",
        WizardRefusal.NotWritten => "The new Kiler could not be written. The console of Kiler says why.",
        WizardRefusal.Partial => "The new Kiler was written only in part. Stop Kiler, delete the data folder and its local copy, and start Kiler again for the wizard. The console of Kiler says where they are and what is missing.",
        WizardRefusal.Stopping => "Kiler is stopping.",
        _ => "The data folder cannot be used now. The console of Kiler says why.",
    };

    private static Results<Ok<WizardResponse>, BadRequest<ErrorResponse>, Conflict<ErrorResponse>> Run(KilerData data, ICurrentAccount account, WizardRequest request)
    {
        var levels = request.Levels;
        var displayName = request.DisplayName;
        var password = request.Password;
        if (!NewKiler.TryCheck(levels, displayName, password, request.AutoLockMinutes, out var invalid))
        {
            return TypedResults.BadRequest(new ErrorResponse(invalid));
        }

        if (account.Name is not { } name)
        {
            return TypedResults.Conflict(new ErrorResponse(NoAccount));
        }

        return data.TryMakeNew(() => NewKiler.Make(name, levels, displayName, password, request.GuestMode, request.AutoLockMinutes), out var made, out var refusal)
            ? TypedResults.Ok(new WizardResponse(made.RecoveryKey))
            : TypedResults.Conflict(new ErrorResponse(Message(refusal)));
    }
}

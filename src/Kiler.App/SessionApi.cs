using Kiler.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kiler.App;

/// <param name="Password">The password of the user file of this Kiler's account.</param>
internal sealed record SignInRequest(string? Password)
{
    /// <summary>The name of the type: a record prints its properties, and this one holds a password (SKILL.md, "Style").</summary>
    public override string ToString() => nameof(SignInRequest);
}

/// <summary>What the page shows of the session (M4.4).</summary>
/// <param name="Account">The account of this Kiler; null when the Windows user name cannot be one, so no one can sign in.</param>
/// <param name="SignedIn">Whether there is a session.</param>
/// <param name="DisplayName">The display name of the session's user file; null without a session.</param>
/// <param name="Levels">The levels that the page reads: 0 and the levels of the session's keys, 0 alone for a guest, none else.</param>
/// <param name="CanEdit">Whether the session can edit: its user file has a sealed <c>canEdit</c> (M4.15).</param>
/// <param name="IsAdmin">
/// Whether the session is an admin: its user file has a sealed <c>isAdmin</c>, and it holds the private key of the admin key
/// (M4.15); the admin actions also need the key of every level.
/// </param>
/// <param name="MustChangePassword">Whether the session has a temporary password, which it changes before anything else (<see cref="PasswordApi"/>, M4.5a).</param>
/// <param name="GuestMode">Whether <c>settings.json</c> has guest mode, so the page opens as a guest with "Sign in".</param>
/// <param name="RightsRefusal">
/// Why the session does not get the rights that its user file says, which the page shows (M4.15): <c>notSealed</c>, a reader
/// whose file does not verify, or <c>noAdminKey</c>, a sealed <c>isAdmin</c> without the private key; null without a session
/// and when it gets them all (<see cref="Session.RightsRefused"/>).
/// </param>
internal sealed record SessionResponse(
    string? Account,
    bool SignedIn,
    string? DisplayName,
    IReadOnlyList<int> Levels,
    bool CanEdit,
    bool IsAdmin,
    bool MustChangePassword,
    bool GuestMode,
    string? RightsRefusal);

/// <summary>
/// <c>/api/session</c> (M4.4, steps 3 and 4 of M4): <c>GET</c> gives what the page shows of the session, <c>POST</c> signs in
/// with the password, and <c>DELETE</c> locks the session, which the "Lock" button and the sign-out alike call: the keys are
/// disposed, and the page shows the guest or the sign-in. Each answers with the session as it is then, and needs nothing, as
/// they are how a session begins. A password that does not fit is 400; a sign-in that cannot run now (no account, no data,
/// the copy opening) is 409; a sign-in that gives no session is 403. Each refusal is an <see cref="ErrorResponse"/>.
/// </summary>
internal static class SessionApi
{
    public const string NoData = "Kiler has no data to sign in to now. The state of the data says why.";

    public static void Map(IEndpointRouteBuilder endpoints, CurrentSession sessions, KilerData data, ICurrentAccount account)
    {
        endpoints.MapGet("/api/session", () => TypedResults.Ok(Describe(sessions, data, account))).Needs(Need.Nothing);
        endpoints.MapPost("/api/session", (SignInRequest request) => Start(sessions, data, account, request)).Needs(Need.Nothing);
        endpoints.MapDelete("/api/session", () =>
        {
            sessions.Lock();
            return TypedResults.Ok(Describe(sessions, data, account));
        }).Needs(Need.Nothing);
    }

    /// <summary>What the page says when a sign-in gives no session.</summary>
    public static string Message(SignInRefusal refusal) => refusal switch
    {
        SignInRefusal.WrongPassword => "The password is wrong.",
        SignInRefusal.NoUser => "This Windows account has no Kiler user. Contact an admin.",
        SignInRefusal.UserInvalid => "The user file of this account cannot be read. Contact an admin.",
        SignInRefusal.LevelsInvalid => "levels.json cannot be read, so no key can be checked. Contact an admin.",
        SignInRefusal.NoAdminPublicKey =>
            "The user file of this account has no public key of the admin key, so Kiler cannot check levels.json. Ask an admin for a new temporary password.",
        SignInRefusal.LevelsNotSealed =>
            "levels.json has been changed outside Kiler and does not carry the admin seal, so no key can be checked. Contact an admin.",
        _ => "No access. Contact an admin.",
    };

    /// <summary>The name of <paramref name="refusal"/> in <see cref="SessionResponse.RightsRefusal"/>.</summary>
    public static string? NameOf(RightsRefusal? refusal) => refusal switch
    {
        null => null,
        RightsRefusal.NotSealed => "notSealed",
        RightsRefusal.NoAdminKey => "noAdminKey",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, "A refusal of the rights without a name."),
    };

    private static Results<Ok<SessionResponse>, BadRequest<ErrorResponse>, Conflict<ErrorResponse>, JsonHttpResult<ErrorResponse>> Start(
        CurrentSession sessions, KilerData data, ICurrentAccount account, SignInRequest request)
    {
        var password = request.Password;
        if (Password.Invalid(password) is { } invalid)
        {
            return TypedResults.BadRequest(new ErrorResponse(invalid));
        }

        if (account.Name is not { } name)
        {
            return TypedResults.Conflict(new ErrorResponse(WizardApi.NoAccount));
        }

        // Read once, so the state and the files are of one moment.
        if (data.Files is not { } files)
        {
            return TypedResults.Conflict(new ErrorResponse(data.State == DataState.Opening ? WizardApi.Message(WizardRefusal.Opening) : NoData));
        }

        if (!SignIn.TryStart(files, name, password, out var session, out var refusal))
        {
            return TypedResults.Json(new ErrorResponse(Message(refusal)), KilerJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status403Forbidden);
        }

        sessions.Start(session);
        return TypedResults.Ok(Describe(sessions, data, account));
    }

    /// <summary>The session as it is now, as the page shows it; also the answer of a change of password (<see cref="PasswordApi"/>).</summary>
    public static SessionResponse Describe(CurrentSession sessions, KilerData data, ICurrentAccount account)
    {
        var guestMode = data.Files?.Settings?.Model?.GuestMode == true;
        return sessions.Session is { IsLocked: false } session
            ? new(account.Name, true, session.DisplayName, session.Levels, session.CanEdit, session.IsAdmin, session.MustChangePassword, guestMode, NameOf(session.RightsRefused))
            : new(account.Name, false, null, guestMode ? [0] : [], false, false, false, guestMode, null);
    }
}

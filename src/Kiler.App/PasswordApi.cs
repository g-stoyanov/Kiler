using Kiler.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kiler.App;

/// <param name="Current">The current password, for a voluntary change; a session with a temporary password needs none.</param>
/// <param name="Password">The new password; the page asks for it twice and checks that the two match.</param>
internal sealed record PasswordRequest(string? Current, string? Password)
{
    /// <summary>The name of the type: a record prints its properties, and this one holds passwords (SKILL.md, "Style").</summary>
    public override string ToString() => nameof(PasswordRequest);
}

/// <summary>
/// <c>POST /api/session/password</c> (M4.5a, step 3 of M4): changes the password of the session (<see cref="PasswordChange"/>),
/// the forced change after a temporary password and the voluntary one with the current password, and answers with the session
/// as it is then, as the sign-in does (<see cref="SessionApi"/>). It needs a session, also one with a temporary password
/// (<see cref="Need.SignedIn"/>). A password that does not fit, or a voluntary change without the current one, is 400; a wrong
/// current password, or a level of the user file that did not unlock, is 403; a change that cannot be made now is 409. Each
/// refusal is an <see cref="ErrorResponse"/>.
/// </summary>
internal static class PasswordApi
{
    public const string CurrentMissing = "Give your current password.";

    public static void Map(IEndpointRouteBuilder endpoints, CurrentSession sessions, KilerData data, ICurrentAccount account) =>
        endpoints.MapPost("/api/session/password", (HttpContext context, PasswordRequest request) => Change(context, sessions, data, account, request))
            .Needs(Need.SignedIn);

    /// <summary>What the page says when the password was not changed.</summary>
    public static string Message(PasswordChangeOutcome outcome) => outcome switch
    {
        PasswordChangeOutcome.WrongPassword => "The current password is wrong.",
        PasswordChangeOutcome.LevelNotUnlocked => "A level of your user file did not unlock with your password, and a new password would lose it. Contact an admin.",
        PasswordChangeOutcome.UserChanged => "Your user file has changed since you signed in. Sign in again.",
        PasswordChangeOutcome.Busy => "Your user file is being written now. Try again in a moment.",
        PasswordChangeOutcome.Offline => "The shared folder cannot be reached. The password can be changed once it is back.",
        _ => "Your user file could not be written to the shared folder. Try again in a moment, or contact an admin.",
    };

    private static Results<Ok<SessionResponse>, BadRequest<ErrorResponse>, Conflict<ErrorResponse>, JsonHttpResult<ErrorResponse>> Change(
        HttpContext context, CurrentSession sessions, KilerData data, ICurrentAccount account, PasswordRequest request)
    {
        // Need.SignedIn lets no request through without a session.
        var session = AccessRules.SessionOf(context) ?? throw new InvalidOperationException("The change of password has no session.");
        var password = request.Password;
        var current = request.Current;
        if ((Password.Invalid(password) ?? (current is null ? null : Password.Invalid(current))) is { } invalid)
        {
            return TypedResults.BadRequest(new ErrorResponse(invalid));
        }

        if (current is null && !session.MustChangePassword)
        {
            return TypedResults.BadRequest(new ErrorResponse(CurrentMissing));
        }

        // Read once, so the state and the team are of one moment.
        if (data.Files is null || data.Team is not { } team)
        {
            return TypedResults.Conflict(new ErrorResponse(data.State == DataState.Opening ? WizardApi.Message(WizardRefusal.Opening) : SessionApi.NoData));
        }

        var outcome = PasswordChange.Run(session, team, current, password);
        return outcome switch
        {
            PasswordChangeOutcome.Changed => TypedResults.Ok(SessionApi.Describe(sessions, data, account)),
            PasswordChangeOutcome.WrongPassword or PasswordChangeOutcome.LevelNotUnlocked =>
                TypedResults.Json(new ErrorResponse(Message(outcome)), KilerJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status403Forbidden),
            _ => TypedResults.Conflict(new ErrorResponse(Message(outcome))),
        };
    }
}

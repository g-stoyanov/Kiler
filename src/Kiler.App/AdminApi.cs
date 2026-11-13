using Kiler.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kiler.App;

/// <summary>A user file as the admin's list shows it (M4.6).</summary>
/// <param name="Account">The account, the id of the user file.</param>
/// <param name="DisplayName">The display name; null, as the values below, when the file does not fit.</param>
/// <param name="Level">The level that the file says; the keys are what the user reads (design 7.8).</param>
/// <param name="CanEdit">Whether the file has <c>canEdit</c>.</param>
/// <param name="IsAdmin">Whether the file has <c>isAdmin</c>.</param>
/// <param name="MustChangePassword">Whether the file has a temporary password, which the user changes at the sign-in.</param>
/// <param name="KeysMatchLevel">
/// Whether the keys of the file are those of the levels 1 to its level (<see cref="AdminActions.KeysMatchLevel"/>); false is
/// the mark of a level without its key, or a key above it.
/// </param>
/// <param name="Sealed">
/// Whether the admin key of the session sealed the rights of the file (M4.14): the panel offers only the top level for a
/// new temporary password of a sealed admin, as an action makes any other file a reader; false for a file that does not fit.
/// </param>
/// <param name="Invalid">Why the file does not fit or is damaged; null when it fits.</param>
internal sealed record AdminUser(
    string Account, string? DisplayName, int? Level, bool CanEdit, bool IsAdmin, bool MustChangePassword, bool KeysMatchLevel, bool Sealed, string? Invalid);

/// <param name="Users">The user files of the team, in the order of the accounts.</param>
/// <param name="FewAdmins">
/// Whether the team has fewer than two admins whose <c>isAdmin</c> the admin key sealed, so the list warns (design 7.7, M4.14).
/// </param>
internal sealed record AdminUsersResponse(IReadOnlyList<AdminUser> Users, bool FewAdmins);

/// <param name="Account">The Windows user name of the new user, which becomes the account as their Kiler makes it.</param>
/// <param name="DisplayName">The display name.</param>
/// <param name="Level">The level, from 1 to the top level.</param>
/// <param name="CanEdit">Whether the user is an editor.</param>
internal sealed record NewUserRequest(string? Account, string? DisplayName, int Level, bool CanEdit);

/// <param name="Level">
/// The level of the new temporary password, from 1 to the top level: a change of level, or the user's level again for a
/// forgotten password. The page sends it, never Kiler from the user file, which its user can write (M4.6).
/// </param>
internal sealed record LevelRequest(int Level);

/// <param name="CanEdit">Whether the user is an editor.</param>
/// <param name="IsAdmin">Whether the user is an admin, which gives them the top level with a new temporary password.</param>
/// <param name="Shown">
/// The rights of the file as the list showed them, which the action compares with the file (M4.14 security review); a
/// request without both is 400.
/// </param>
internal sealed record RightsRequest(bool CanEdit, bool IsAdmin, ShownRights? Shown);

/// <param name="CanEdit">The editor right of the file as the list showed it.</param>
/// <param name="IsAdmin">The admin right of the file as the list showed it.</param>
internal sealed record ShownRights(bool? CanEdit, bool? IsAdmin);

/// <param name="Name">The new name of the level.</param>
internal sealed record RenameRequest(string? Name);

/// <param name="User">The user file as the action left it.</param>
/// <param name="TemporaryPassword">
/// The temporary password that the action made, which only this answer gives (kiler-crypto, "Passwords"); null when the action
/// gave no keys.
/// </param>
internal sealed record AdminUserResponse(AdminUser User, string? TemporaryPassword)
{
    /// <summary>The name of the type: a record prints its properties, and this one holds a password (SKILL.md, "Style").</summary>
    public override string ToString() => nameof(AdminUserResponse);
}

/// <summary>
/// <c>/api/admin</c> (M4.6, step 7 of M4): the list of users with the marks and the warning, a new user, a new temporary
/// password with the level that the admin gives (a change of level, or a forgotten password), the editor and admin rights
/// (<see cref="AdminActions"/>, each sealed by the admin key of the session, M4.14), and the levels with the rename of a level.
/// Each needs an admin (<see cref="Need.Admin"/>). An action that gives keys answers with the temporary password, once. A
/// value that does not fit, the admin's own account and an admin's level below the top level are 400; a <c>levels.json</c>
/// with other checks than the sign-in is 403, and the page asks for a new sign-in; a session without the admin key is 403
/// too, and the page reads the session again; an action that cannot be done now, or on a file that the admin key did not
/// seal, is 409. Each refusal is an <see cref="ErrorResponse"/>.
/// </summary>
internal static class AdminApi
{
    /// <summary>The reason of a change of the rights without the rights that the list showed.</summary>
    public const string ShownMissing = "shown is missing, or has no canEdit or isAdmin.";

    public static void Map(IEndpointRouteBuilder endpoints, KilerData data)
    {
        endpoints.MapGet("/api/admin/users", (HttpContext context) => Users(context, data)).Needs(Need.Admin);
        endpoints.MapPost("/api/admin/users", (HttpContext context, NewUserRequest request) =>
            Act(context, data, (admin, team) => AdminActions.NewUser(admin, team, request.Account, request.DisplayName, request.Level, request.CanEdit)))
            .Needs(Need.Admin);
        endpoints.MapPut("/api/admin/users/{account}/level", (HttpContext context, string account, LevelRequest request) =>
            Act(context, data, (admin, team) => AdminActions.ChangeLevel(admin, team, account, request.Level)))
            .Needs(Need.Admin);
        endpoints.MapPut("/api/admin/users/{account}/rights", (HttpContext context, string account, RightsRequest request) =>
            Act(context, data, (admin, team) => request.Shown is { CanEdit: { } shownCanEdit, IsAdmin: { } shownIsAdmin }
                ? AdminActions.SetRights(admin, team, account, request.CanEdit, request.IsAdmin, shownCanEdit, shownIsAdmin)
                : new AdminResult(AdminOutcome.Invalid, ShownMissing)))
            .Needs(Need.Admin);
        endpoints.MapGet("/api/admin/levels", (HttpContext context) => Levels(context, data)).Needs(Need.Admin);
        endpoints.MapPut("/api/admin/levels/{level}", (HttpContext context, int level, RenameRequest request) => Rename(context, data, level, request))
            .Needs(Need.Admin);
    }

    /// <summary>What the page says when an admin action was refused, but for a value that does not fit, whose reason is its own.</summary>
    public static string Message(AdminOutcome outcome) => outcome switch
    {
        AdminOutcome.OwnAccount => "An admin does not change their own user file here; another admin does. Your own password is changed on the password screen.",
        AdminOutcome.NoUser => "The account has no user file. Read the list again.",
        AdminOutcome.Unreadable => "The user file cannot be read, so it cannot be changed.",
        AdminOutcome.Exists => "The account has a user file already, or had one.",
        AdminOutcome.AdminLevel => "An admin has the top level. Take the admin right away first.",
        AdminOutcome.LevelsChanged => AccessRules.Message(AccessRefusal.LevelsChanged),
        AdminOutcome.NoAdminKey => "Your user file has no admin key, so Kiler cannot seal the change. Another admin gives you a new temporary password.",
        AdminOutcome.UserNotSealed =>
            "The user file is not sealed by an admin, so its rights are not changed. Give the user a new temporary password first: it seals the file as a reader, whose rights can then be set.",
        AdminOutcome.LevelsNotSealed =>
            "levels.json in the shared folder is not the version that an admin sealed last, so a rename would build on a change that someone made in it. Put back its sealed version first.",
        AdminOutcome.Changed => "The file has changed meanwhile. Read it again and try once more.",
        AdminOutcome.Busy => "The file is being written now. Try again in a moment.",
        AdminOutcome.Offline => "The shared folder cannot be reached. The change can be made once it is back.",
        _ => "The file could not be written to the shared folder. Try again in a moment.",
    };

    private static Results<Ok<AdminUsersResponse>, Conflict<ErrorResponse>> Users(HttpContext context, KilerData data)
    {
        // Need.Admin lets no request through without a session.
        var admin = AccessRules.SessionOf(context) ?? throw new InvalidOperationException("The list of users has no session.");
        if (data.Files is not { } files)
        {
            return TypedResults.Conflict(new ErrorResponse(SessionApi.NoData));
        }

        var users = files.Users.Select(pair => pair.Value.IsValid
            ? Describe(pair.Value.Model, admin)
            : new AdminUser(pair.Key, null, null, false, false, false, false, false, pair.Value.Invalid)).ToList();
        return TypedResults.Ok(new AdminUsersResponse(users, AdminActions.FewAdmins(files, admin.AdminPublicKey)));
    }

    /// <summary>The newest <c>levels.json</c> that the admin key of the session sealed, on the share or in the local copy (M4.16a), as the admin actions take it.</summary>
    private static Results<Ok<LevelsResponse>, Conflict<ErrorResponse>> Levels(HttpContext context, KilerData data) =>
        data.Files?.LevelsFor(AccessRules.SessionOf(context)) is { } levels
            ? TypedResults.Ok(LevelsResponse.Of(levels))
            : TypedResults.Conflict(new ErrorResponse(SessionApi.NoData));

    private static Results<Ok<AdminUserResponse>, BadRequest<ErrorResponse>, Conflict<ErrorResponse>, JsonHttpResult<ErrorResponse>> Act(
        HttpContext context, KilerData data, Func<Session, Team, AdminResult> action)
    {
        // Need.Admin lets no request through without a session.
        var admin = AccessRules.SessionOf(context) ?? throw new InvalidOperationException("An admin action has no session.");
        if (data.Team is not { } team)
        {
            return TypedResults.Conflict(new ErrorResponse(SessionApi.NoData));
        }

        var result = action(admin, team);

        // An action on a user gives the user file when it is done.
        return Answer(result, () => new AdminUserResponse(Describe(result.User!, admin), result.TemporaryPassword));
    }

    private static Results<Ok<LevelsResponse>, BadRequest<ErrorResponse>, Conflict<ErrorResponse>, JsonHttpResult<ErrorResponse>> Rename(
        HttpContext context, KilerData data, int level, RenameRequest request)
    {
        var admin = AccessRules.SessionOf(context) ?? throw new InvalidOperationException("The rename of a level has no session.");
        if (data.Team is not { } team)
        {
            return TypedResults.Conflict(new ErrorResponse(SessionApi.NoData));
        }

        var result = AdminActions.RenameLevel(admin, team, level, request.Name);

        // A rename gives the levels when it is done.
        return Answer(result, () => LevelsResponse.Of(result.Levels!));
    }

    private static Results<Ok<T>, BadRequest<ErrorResponse>, Conflict<ErrorResponse>, JsonHttpResult<ErrorResponse>> Answer<T>(AdminResult result, Func<T> done) =>
        result.Outcome switch
        {
            AdminOutcome.Done => TypedResults.Ok(done()),
            AdminOutcome.Invalid => TypedResults.BadRequest(new ErrorResponse(result.Invalid ?? Message(result.Outcome))),
            AdminOutcome.OwnAccount or AdminOutcome.AdminLevel => TypedResults.BadRequest(new ErrorResponse(Message(result.Outcome))),
            AdminOutcome.LevelsChanged or AdminOutcome.NoAdminKey =>
                TypedResults.Json(new ErrorResponse(Message(result.Outcome)), KilerJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status403Forbidden),
            _ => TypedResults.Conflict(new ErrorResponse(Message(result.Outcome))),
        };

    /// <summary>The user file as the list shows it to <paramref name="admin"/>, whose public key verifies its seal.</summary>
    private static AdminUser Describe(UserFile user, Session admin) =>
        new(
            user.Account,
            user.DisplayName,
            user.Level,
            user.CanEdit,
            user.IsAdmin,
            user.MustChangePassword,
            AdminActions.KeysMatchLevel(user),
            admin.AdminPublicKey is { } publicKey && user.IsSealedBy(publicKey),
            null);
}

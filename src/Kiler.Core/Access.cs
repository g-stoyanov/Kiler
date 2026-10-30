namespace Kiler.Core;

/// <summary>What an endpoint needs (M4.4, step 9 of M4): each one states it, and the server checks it on every request.</summary>
public enum Need
{
    /// <summary>Nothing beyond the cookie: the state of the data, the sign-in and the session, the wizard, the health check and the event stream.</summary>
    Nothing,

    /// <summary>A session, also one with a temporary password, which it can only change: the change of password (M4.5a).</summary>
    SignedIn,

    /// <summary>A guest, while <c>settings.json</c> has guest mode, who reads level 0; or a session, which reads the levels of its keys.</summary>
    Reader,

    /// <summary>A session whose user file has <c>canEdit</c>.</summary>
    Editor,

    /// <summary>
    /// A session whose user file has <c>isAdmin</c>, and which holds the key of every level of <c>levels.json</c>, verified at
    /// the sign-in against the checks that it has now (<see cref="Session.HoldsEvery"/>, M4.6).
    /// </summary>
    Admin,
}

/// <summary>Why a request does not get what its endpoint needs (<see cref="Access.Check"/>).</summary>
public enum AccessRefusal
{
    /// <summary>No session, and the endpoint needs one, or guest mode is off.</summary>
    NotSignedIn,

    /// <summary>The session has a temporary password, so it can only change it (<see cref="Need.SignedIn"/>, M4.5a).</summary>
    MustChangePassword,

    /// <summary>The session cannot edit.</summary>
    CannotEdit,

    /// <summary>The session is not an admin, or does not hold the key of every level.</summary>
    NotAdmin,

    /// <summary>
    /// An admin whose keys verified at the sign-in against other checks than <c>levels.json</c> has now (M4.6): the session
    /// signs in again, so no admin action writes a check planted meanwhile back under the admin's name.
    /// </summary>
    LevelsChanged,
}

/// <summary>
/// The rights of a request (M4.4, design 7.4, decided at the start of M4). <c>canEdit</c> and <c>isAdmin</c> come from the
/// user file of the sign-in: a user who writes them into their own file gets the screens, not the keys (design 7.8). So an
/// admin needs the key of every level too, which only an admin's file holds, until the seal after the exam.
/// </summary>
public static class Access
{
    /// <summary>
    /// Why a request with <paramref name="session"/> does not get what its endpoint needs; <c>null</c> when it does. A guest is
    /// a request without a session while the settings have guest mode. <paramref name="files"/> are the files of the team, or
    /// <c>null</c> while there are none (no data, or a copy that is opening), so there is no guest mode and no admin then.
    /// </summary>
    public static AccessRefusal? Check(Need need, Session? session, TeamFiles? files)
    {
        if (need == Need.Nothing)
        {
            return null;
        }

        if (session is null || session.IsLocked)
        {
            return need == Need.Reader && files?.Settings?.Model?.GuestMode == true ? null : AccessRefusal.NotSignedIn;
        }

        if (need == Need.SignedIn)
        {
            return null;
        }

        if (session.MustChangePassword)
        {
            return AccessRefusal.MustChangePassword;
        }

        return need switch
        {
            Need.Reader => null,
            Need.Editor => session.CanEdit ? null : AccessRefusal.CannotEdit,
            Need.Admin => !session.IsAdmin || files?.Levels?.Model is not { } levels || !Enumerable.Range(1, levels.Top).All(session.Reads)
                ? AccessRefusal.NotAdmin
                : session.HoldsEvery(levels) ? null : AccessRefusal.LevelsChanged,
            _ => throw new ArgumentOutOfRangeException(nameof(need), need, "An endpoint needs one of the values of Need."),
        };
    }
}

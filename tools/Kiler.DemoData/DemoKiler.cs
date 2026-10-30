using Kiler.Core;
using Kiler.Storage;
using Microsoft.Win32.SafeHandles;

namespace Kiler.DemoData;

/// <summary>
/// The made-up Kiler of the demo data (PLAN M8 step 4, M4.10): the levels Guest, Team, Test and Prod, settings with guest
/// mode, an admin and two users with demo passwords. It is made through the code of the wizard and of the admin screens, so
/// it opens and decrypts as a Kiler that a team made. Nothing in it is real (CLAUDE.md, "Data and the public repo").
/// </summary>
internal static class DemoKiler
{
    public const bool GuestMode = true;

    public const int AutoLockMinutes = 30;

    /// <summary>The names of the levels 0 to the top level.</summary>
    public static IReadOnlyList<string> LevelNames { get; } = ["Guest", "Team", "Test", "Prod"];

    /// <summary>The first admin, whom the wizard's code makes, at the top level.</summary>
    public static DemoUser Admin { get; } = new("demo.admin", "Demo Admin", Level: 3, CanEdit: true, IsAdmin: true, "admin-demo");

    /// <summary>The demo users, the admin first; the others the admin makes.</summary>
    public static IReadOnlyList<DemoUser> Users { get; } =
    [
        Admin,
        new("demo.editor", "Demo Editor", Level: 2, CanEdit: true, IsAdmin: false, "editor-demo"),
        new("demo.reader", "Demo Reader", Level: 1, CanEdit: false, IsAdmin: false, "reader-demo"),
    ];

    /// <summary>
    /// Writes the demo Kiler into <paramref name="dataFolder"/>, which <see cref="DataFolder.Create"/> makes: a new folder, or
    /// one with only the empty folders of a data folder. The documents are written through a local copy in
    /// <paramref name="workFolder"/>, an empty folder that the caller deletes afterwards; Kiler makes a copy of its own at its
    /// first start on the data folder. The recovery key is not kept, as nothing restores with it before the exam. PBKDF2 runs
    /// three times for the levels and the admin, and three times for each other user (core.md, the M4.6 note).
    /// </summary>
    /// <exception cref="InvalidOperationException">A step did not end as it should; the message says which.</exception>
    public static void Make(string dataFolder, string workFolder)
    {
        var copy = DataCopy.Open(DataFolder.Create(dataFolder, new NoOwners()), workFolder, TimeProvider.System);
        var made = NewKiler.Make(Admin.Account, LevelNames, Admin.DisplayName, Admin.Password, GuestMode, AutoLockMinutes).Write(copy);
        if (made.Outcome != NewKilerOutcome.Made)
        {
            throw new InvalidOperationException($"The demo Kiler was not written ({made.Outcome}): {made.Failure}");
        }

        var team = new Team(copy);
        using var admin = SignInAs(team, Admin.Account, Admin.Password);
        foreach (var user in Users.Where(user => user != Admin))
        {
            var added = AdminActions.NewUser(admin, team, user.Account, user.DisplayName, user.Level, user.CanEdit);
            if (added is not { Outcome: AdminOutcome.Done, TemporaryPassword: { } temporary })
            {
                throw new InvalidOperationException($"{user.Account} was not made: {added.Outcome}.");
            }

            // The forced change of the temporary password, so the demo user signs in with their demo password and no change.
            using var session = SignInAs(team, user.Account, temporary);
            var changed = PasswordChange.Run(session, team, current: null, user.Password);
            if (changed != PasswordChangeOutcome.Changed)
            {
                throw new InvalidOperationException($"The password of {user.Account} was not changed: {changed}.");
            }
        }
    }

    private static Session SignInAs(Team team, string account, string password) =>
        SignIn.TryStart(team.Files, account, password, out var session, out var refusal)
            ? session
            : throw new InvalidOperationException($"{account} did not sign in: {refusal}.");

    /// <summary>No owners: the copy that writes the demo data is deleted, and Kiler reads the owners at its own first load.</summary>
    private sealed class NoOwners : IFileOwner
    {
        public string? Of(SafeFileHandle file) => null;
    }
}

/// <summary>
/// A demo user and their demo password, which the README will give (PLAN M8 step 6): the account, the id of the user file,
/// which <c>--account</c> takes, and the level, whose keys from 1 the user gets.
/// </summary>
internal sealed record DemoUser(string Account, string DisplayName, int Level, bool CanEdit, bool IsAdmin, string Password)
{
    /// <summary>The account: a record prints its properties, and this one holds a password (SKILL.md, "Style").</summary>
    public override string ToString() => Account;
}

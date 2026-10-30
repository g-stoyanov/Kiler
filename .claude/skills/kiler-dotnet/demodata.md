# tools/Kiler.DemoData: the demo data

Part of the `kiler-dotnet` skill. Read it with [SKILL.md](SKILL.md) when a task changes `tools/Kiler.DemoData` or its tests, such as the templates and records that M5 adds. It calls Kiler.Core, so read [core.md](core.md) too.

## The tool (M4.10)

- A console exe that makes a made-up Kiler in a new data folder (PLAN M8 step 4): `dotnet run --project tools\Kiler.DemoData -- <data folder> [--replace]`, from the repository root for `dev-data`, the data folder of `npm run dev` (kiler-ui, "Dev mode"). It is in `Kiler.slnx`, so the build, the format check, the tests and CI take it; it is not published.
- Everything is made through the code that Kiler runs, never by writing JSON by hand, so the data opens and decrypts as a Kiler that a team made: `NewKiler.Make` and `Write` for the levels, the settings and the admin; the admin's session from `SignIn.TryStart`; `AdminActions.NewUser` for each other user, then that user's `SignIn.TryStart` with the temporary password and `PasswordChange.Run`, the forced change, so the user signs in with the demo password and no change. Each other user costs three PBKDF2, so a run takes a few seconds.
- The documents are written through a local copy in a temporary work folder, which the tool deletes; Kiler makes a copy of its own at its first start. The owners are not read (`NoOwners`). The recovery key is not kept, as nothing restores with it before the exam.
- `DemoKiler` holds what the demo is: the levels Guest, Team, Test and Prod; guest mode on and 30 minutes of the lock; and `DemoKiler.Users` (decided by the user in M4.10). Everything in it is made up, and the passwords are public, for the README and the screenshots; the tool prints them.

| Account | Name | Level | Rights | Password |
|---|---|---|---|---|
| `demo.admin` | Demo Admin | 3 Prod | admin, editor | `admin-demo` |
| `demo.editor` | Demo Editor | 2 Test | editor | `editor-demo` |
| `demo.reader` | Demo Reader | 1 Team | reader | `reader-demo` |

## The place (M4.10, decided by the user)

- `DemoTarget` is the data folder and its local copy, `%LOCALAPPDATA%\Kiler\copies\<key>`. The copy is per path, so a copy that a Kiler made of an earlier data folder at the same path, such as a `dev-data` of the wizard, keeps that folder's documents. So without `--replace` the tool refuses when either is there, and with it deletes both first.
- `DemoTarget.CopyKey` repeats `InstancePlace.Key` of Kiler.App, which is internal there, and the constants `copies` and `lock` repeat `DataPlace.CopiesSubfolder` and `KilerData.LockFile`. A test of each holds the key of `D:\Demo\data` to the same value (`DemoTargetTests`, `InstancePlaceTests`); a change of the key changes both.
- `--replace` deletes only what cannot be a mistake: a data folder on a local fixed disk (checked before any look at the disk, so a network path is never touched), not a link, holding at its top nothing but what a data folder holds; and nothing while a Kiler holds `<copy>\lock`. A dev Kiler that runs without the data folder holds no lock, so stop it before the run, or it stays without data until its next start.

## Tests

- `Kiler.DemoData.Tests` makes the demo once per class (`DemoKilerTests.Made`) and opens it through a new local copy and `Team`, as Kiler opens it at its first start; each demo user signs in with their password. `DemoTargetTests` work in a temporary folder, with a local folder of their own, never the real profile.

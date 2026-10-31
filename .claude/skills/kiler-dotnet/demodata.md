# tools/Kiler.DemoData: the demo data

Part of the `kiler-dotnet` skill. Read it with [SKILL.md](SKILL.md) when a task changes `tools/Kiler.DemoData` or its tests, such as the stashes and records of M5.5. It calls Kiler.Core, so read [core.md](core.md) too.

## The tool (M4.10)

- A console exe that makes a made-up Kiler in a new data folder (PLAN M8 step 4): `dotnet run --project tools\Kiler.DemoData -- <data folder> [--replace] [--volume]`, from the repository root for `dev-data`, the data folder of `npm run dev` (kiler-ui, "Dev mode"). It is in `Kiler.slnx`, so the build, the format check, the tests and CI take it; it is not published.
- Everything is made through the code that Kiler runs, never by writing JSON by hand, so the data opens and decrypts as a Kiler that a team made: `NewKiler.Make` and `Write` for the levels, the settings, the admin and the four initial templates (M5.2), at the levels 0 to 3 of the demo; the admin's session from `SignIn.TryStart`; `AdminActions.NewUser` for each other user, then that user's `SignIn.TryStart` with the temporary password and `PasswordChange.Run`, the forced change, so the user signs in with the demo password and no change. Each other user costs three PBKDF2, so a run takes a few seconds.
- The documents are written through a local copy in a temporary work folder, which the tool deletes; Kiler makes a copy of its own at its first start. The owners are not read (`NoOwners`). The recovery key is not kept, as nothing restores with it before the exam.
- `DemoKiler` holds what the demo is: the levels Guest, Team, Test and Prod; guest mode on and 30 minutes of the lock; and `DemoKiler.Users` (decided by the user in M4.10). Everything in it is made up, and the passwords are public, for the README and the screenshots; the tool prints them.

| Account | Name | Level | Rights | Password |
|---|---|---|---|---|
| `demo.admin` | Demo Admin | 3 Prod | admin, editor | `admin-demo` |
| `demo.editor` | Demo Editor | 2 Test | editor | `editor-demo` |
| `demo.reader` | Demo Reader | 1 Team | reader | `reader-demo` |

## The stashes and records (M5.5)

- `DemoRecords` holds them: `Stashes` (made-up systems, `Prod`, `Test` and `Dev`, and areas such as `Reports`), each with its author, and `Set`, 31 records of the four initial templates with tags, in paths of 1 to 4 stashes, at the levels 0 to 3, with a few fields raised (`Levels`) and some records and a stash by `demo.editor`. Every host is under `example.com`, and every password is made up. A `ref` names its record by its name (`DemoRecord.Refs`), so the names are unique, and the record it names comes before it for the same author.
- `DemoRecords.Write` makes them after the users, through `RecordActions.NewStash` and `NewRecord` (core.md, "Templates, stashes and records"), over a `Records` of the work copy with a `CurrentSession` that is never started, each by the session of its author: the admin's, and the editor's, who signs in again with the demo password. The stashes come first, then the records of each author together, as `Records.Read` decrypts the whole set again at each new author. A refused write is an `InvalidOperationException` with the record's name and the sentence of the refusal, never a value. The editor holds level 2, so a record of the editor has no field at 3: a `prod` database is the admin's.
- The volume mode, `--volume` (`DemoOptions.Volume`), makes `DemoRecords.VolumeCount`, 2000 records in all, for the timing of the search (M5.6): the demo set, and made-up records by the admin, in groups of the four templates, under 40 more stashes (`VolumeSystems`: `System 01` to `System 40`), each with `Prod`, `Test` and `Dev` under it. A write costs some 14 ms in the layer (the share, the copy and its index, M2.7), the same at the first and the 2000th, so the volume mode takes some 33 s and the demo some 3 s; the tool prints the time.

## The place (M4.10, decided by the user)

- `DemoTarget` is the data folder and its local copy, `%LOCALAPPDATA%\Kiler\copies\<key>`. The copy is per path, so a copy that a Kiler made of an earlier data folder at the same path, such as a `dev-data` of the wizard, keeps that folder's documents. So without `--replace` the tool refuses when either is there, and with it deletes both first.
- `DemoTarget.CopyKey` repeats `InstancePlace.Key` of Kiler.App, which is internal there, and the constants `copies` and `lock` repeat `DataPlace.CopiesSubfolder` and `KilerData.LockFile`. A test of each holds the key of `D:\Demo\data` to the same value (`DemoTargetTests`, `InstancePlaceTests`); a change of the key changes both.
- `--replace` deletes only what cannot be a mistake: a data folder on a local fixed disk (checked before any look at the disk, so a network path is never touched), not a link, holding at its top nothing but what a data folder holds; and nothing while a Kiler holds `<copy>\lock`. A dev Kiler that runs without the data folder holds no lock, so stop it before the run, or it stays without data until its next start.

## Tests

- `Kiler.DemoData.Tests` makes the demo once per class, with a fixture from `DemoFolder` (`DemoKilerTests.Made`, and `DemoVolumeTests.Made` for the volume mode, some 35 s), and opens it through a new local copy and `Team`, as Kiler opens it at its first start; each demo user signs in with their password (`DemoFolder.SignIn`), and `DemoFolder.Read` gives the records as a session or the guest reads them, through a `Records` of its own. A test compares the values that a user reads with those of `DemoRecords.Set`, and never prints a `secret`, which the view does not keep anyway. `DemoTargetTests` work in a temporary folder, with a local folder of their own, never the real profile.
- The E2E tests of the screens (M6.12, host.md, "E2E tests") write the demo into the data folder of each test with `DemoKiler.Make`, through `InternalsVisibleTo` of `Kiler.E2E`. They take the counts from `DemoRecords.Set`, but name some records, stashes and tags of the demo (`Billing prod database` and its password at the level Prod, `Unpaid invoices by month`, `Welcome to the team`, `Holiday calendar`, the stashes `Billing`, `Reports`, `Handbook`, `Orders` and the path `Customer portal › Integrations › Payments › Test`, the tag `reporting`): a change of those changes the E2E tests too, so run `e2e.ps1` after one.

# Checks in the real environment

Two sets of checks on the real shared folder: the checkpoint right after M1 (sections 1 to 4, done), and [the checks of the trial use](#the-trial-use-phase-4) in phase 4, which need what the developer machine did not have at the end of M2.

The checkpoint right after M1: the checks of design section 14, now with the real `Kiler.exe` instead of the probe of M1.0b. The results of the probe are in the notes under M1.0b in [tasks/log/M1.md](tasks/log/M1.md); these checks confirm them and add what the probe could not show.

Take `Kiler.exe` from the artifact of a CI push run on main and compare its SHA-256 with the one on the run page, or publish it with `publish.ps1`. The run page shows the SHA-256 twice: in the job summary, when signed in, and as the digest of the artifact. The artifact is the exe itself, not a zip; `gh run download` fails on it, so from the command line take it with `gh api repos/g-stoyanov/Kiler/actions/artifacts/<artifact id>/zip > Kiler.exe` (M1.12). Run everything as a normal user, not as administrator.

## 1. Start from the shared folder

1. Copy `Kiler.exe` into the shared folder, for example `\\server\share\Kiler`.
2. Double click it in Explorer. Is there a SmartScreen or "Open File – Security Warning" dialog, an AppLocker message or an antivirus alert?
3. The first start copies itself to `%LOCALAPPDATA%\Programs\Kiler`, writes the shared folder to `%LOCALAPPDATA%\Kiler\share.json`, makes `Kiler.lnk` in the Start menu, starts the copy and opens the browser. The page says "Connected". How long does it take to the page?
4. A second double click while Kiler runs opens a new tab to the same server.
5. The Start menu shortcut starts the copy, without copying. How long does it take to the page?

To undo it, delete `%LOCALAPPDATA%\Programs\Kiler`, `%LOCALAPPDATA%\Kiler\share.json` and `Kiler.lnk` in `%APPDATA%\Microsoft\Windows\Start Menu\Programs`.

## 2. Many small files: `--diag`

From a console (cmd or PowerShell):

```powershell
& '\\server\share\Kiler\Kiler.exe' --diag '\\server\share\Kiler'
```

`--diag` runs where it is started, also from the shared folder: it starts no server, opens no browser and copies nothing. The folder must exist. Without a folder it takes the data folder: `--data <folder>` when given, else `data\` in the shared folder, which is the folder of the exe when it runs from the shared folder, and the folder in `share.json` when it runs from the local copy.

In a temporary subfolder `kiler-diag-<time>-<hex>` of that folder it:

- writes 2000 small JSON files one by one;
- checks the owner of the new subfolder and of a new file;
- replaces one file by renaming a temporary file over it (design 5.4), and checks the owner again;
- lists the files with names and dates three times;
- reads the files in four parts, one by one and 8, 16 and 32 at a time, then the first part again;
- deletes only the files it created, then the subfolder when it is empty.

The subfolder is held open until its files are deleted, so during the run (one or two minutes on the shared folder) no one can rename or delete it. On a local NTFS disk no one can rename a folder above it either; the file server of the shared folder lets one be renamed (M2.11).

The report goes to the console and to `Kiler-diag-<time>-<hex>.txt` next to the exe. The file shows `<test folder>`, `<exe folder>`, `<profile>`, `<user>` and `<another account>` in place of the folders and the account names, because it may stay on the shared folder, which everyone can read. Delete the reports from the shared folder once they are read.

Exit codes: 0 when every check passed, 1 when a check failed, the folder cannot be used or the run was stopped. The first Ctrl+C stops the run and cleans up. A second one ends the process at once and leaves the subfolder; then delete it by hand.

On a local disk, a run as administrator can show the Administrators group as the owner instead of the user.

## 3. The shared folder, by hand

Right click the shared folder → **Properties**:

1. **Previous Versions**: are there automatic copies, and how often? Check the parent folder too.
2. **DFS**: is the path DFS? Are there several referral targets (servers)? Several targets can mean replication between servers, and with it delays or conflicts between users on different targets.
3. **Security** → **Advanced**: who owns the folder and who manages the permissions? Can ordinary users change and replace files that other users created, the exe among them?

## 4. DLLs next to the exe

Only if Process Monitor (Sysinternals) may run on the machine. Filter on the process `Kiler.exe` and on paths in the shared folder that end with `.dll`, then start Kiler from the shared folder four ways:

- the first start, with no local copy yet (it copies and makes the shortcut through COM);
- a start when the local copy exists (it only starts the copy);
- a second start while Kiler runs (it asks the running Kiler and opens the browser through the shell);
- `Kiler.exe --diag` (it reads the owners of files and the names of accounts, which a server start does not).

Which DLL names does it look for in the shared folder? The imports of the exe look only in System32 (M1.8). On the local disk the runtime still looked for `BCrypt.dll`, `ntdll.dll` and `NTASN1.dll` next to the exe (the residual risk under M1.8). The answer feeds the check of the first copy in M3.

Process Monitor needs administrator rights. Without them, the checkpoint after M1 put copies of the System32 DLLs that the runs load next to the exe in the shared folder, read the module list of each of the four runs every few milliseconds, and deleted the copies afterwards. That shows what was loaded from the shared folder, not every name that was looked for.

## Results

A summary without internal names, hosts, paths or accounts goes into [tasks/M1.md](tasks/M1.md#checkpoint-in-the-real-environment), as the results of M1.0b went into its notes, and into the course notes.

---

## The trial use (phase 4)

The checks of the data layer that need what the developer machine did not have at the end of M2 (M2.11b): the network off, a second machine, another account, a machine that sleeps, and the rights that IT sets on the shared folder, which gave Everyone full control until then. Each line names the task whose note asks it; the history is in [tasks/log/M2.md](tasks/log/M2.md), searched by the task number. A finding that changes a decision is decided with the user.

- The path of the shared folder is an internal name, so it is in no file of the repo: ask the user for it. The tests and the probes make a subfolder `kiler-tests-<time>` in it and delete it at the end.
- While the network is off, the session of Claude may have none either, so a script writes what it measures into a log, which is read afterwards.
- A summary without internal names goes into a note of the task that runs the checks, and into the course notes.

### The tests of the file server

`FileServerTests` and `FileServerMeasures` of Kiler.Storage.Tests, and `FileServerMeasures` of Kiler.App.Tests, with `KILER_TEST_FOLDER` naming a subfolder of the shared folder, set in the process that runs them, the last with `KILER_SMOKE_EXE` too (`kiler-dotnet`: storage.md, "Tests of the layer", and host.md, "Smoke tests"). A failure says which answer of the file server has changed; the numbers compare with those of the M2.11c note. The whole Kiler.Storage.Tests and the `FileOwnerTests` of Kiler.App.Tests run there too, as in M2.11, so the holds and writes meet the rights that IT sets. Run them again after a move to another file server too. They take long there, the whole Kiler.Storage.Tests about 10.5 minutes and the measures about 8 more, so run them in the background, and the measures alone with `-class`, as the classes of a run go in parallel and would change their times.

### The network off

What one machine answered at M2.11b, with the network on: a data folder renamed away on a share that is there makes Kiler offline at the next load (`DirectoryNotFoundException`), not `Failed`, and it comes back by itself once the folder is back. With a file server address that no one answers, the first look blocks about 37 s and fails with an `IOException` (`ERROR_BAD_NETPATH`), the looks right after it fail at once, a try about 75 s later blocked again, for at least 32 s, and the page opens meanwhile; a name that does not resolve fails in 0.2 s. A session to the real file server that goes away can behave otherwise, and that is what these checks show.

Before: a data folder on the shared folder with a few documents, and Kiler started on it once through the UNC path and once through a mapped drive letter, as each has a local copy of its own.

- (M2.8a) Kiler runs and the network goes off (the VPN or the network): how long until it says `The data folder cannot be reached …`, as the first hold of the root holds the lock of the local copy while it blocks; that it says so and not `A load of the data folder failed`, as offline is an `IOException` or an `UnauthorizedAccessException` and anything else is `Failed`; how long the tries after it block, each holding only the root, before the lock (M2.8c); and that it says `The data folder can be reached again.` by itself once the network is back.
- (M2.8a, M2.10) a start with the network off works from the local copy. With the share as a UNC path: how long until the first load says offline (`DataFolder.Open` and the first hold), and that the page opens meanwhile. With the share as a mapped drive letter that is not connected: whether it is offline too, or `the data folder does not exist`, as `DataFolder.Open` refuses a `DirectoryNotFoundException` and Kiler then runs without data, a finding.

### A second machine

- (M2.2, M2.3, M2.4) a rename over a file that the other machine reads gives access denied or a sharing violation, with the retries and `Busy` of M2.3; a hold from the other machine gives a sharing violation, also on the slot of a write under way.
- (M2.4) the write time read through the handle is the file server's, also with the clock of the machine some minutes off. Setting the clock needs administrator rights; `FileServerTests` print the offset of the file server's clock.
- (M2.11a) how often a listing of a history folder on the other machine misses a slot that the first machine has just made: the SMB cache of about 10 s, and a load that misses a slot never takes it (M2.7a). The slots of one machine showed in its own listings right after, 20 of 20.
- (M2.4, M2.5) what the file server does with the handles of a machine that sleeps or loses its session, with a write or a delete under way, or a probe that holds a slot without sharing and a marker. From the other machine: whether the slot is still held (a sharing violation) after 2 minutes and later, and whether the marker goes when the session ends or its open lingers until a durable handle times out. After the wake: whether the calls through the handles fail. When the file server closes the slot's handle, another write can take over the slot after 2 minutes while the write still runs, and the write renames over the document when it wakes; a delete woken so moves the version N+1 of the other write to the trash as N. If a call through such a handle fails after the wake, a check of the slot's handle before each rename over the document closes it (second security review of M2.4), a change of the layer to decide with the user.

### Another account and the rights as IT sets them

With Everyone full control most rights are given anyway, so these count only with the rights that IT sets.

- (M2.5) `OpenTimes` on a document of another account: whether `FILE_WRITE_ATTRIBUTES` is given.
- (M2.6) the holds with `READ_CONTROL` work on folders of another account.
- (M2.7b) every hold of a private folder holds `private\` too, which needs the right to list it: check it with the rights of an ordinary user. A folder with only traverse there makes the private loads and writes fail, reported and with nothing lost.
- (M2.7c) an admin can list and read the private folders of other accounts.
- (M2.7d) the folders that another account makes have its SID as their owner; one account cannot tell this from a file server that gives every file one owner, which would make the owner of a private folder prove nothing. A member of the Administrators group of a Windows file server may get the group as the owner of the folders it makes, the same for all admins, which then tells their private folders apart by the id alone and makes the mark of M4 say nothing; and the same user may get another owner when Kiler runs elevated and when not, which on a local disk is the Administrators group or the user, so `HoldBelow` would refuse a folder right after making it. The work of IT: that a folder keeps its id while the share lives, and what a move of the share, a restore from a backup or a switch of the DFS target does to it, each of which makes every private folder `ForeignFolder` until it is accepted.
- (M2.9c) an admin's write back into the private folder of another account (a temporary file made in its `records\` and renamed, a marker in its history folder, the file there deleted through the hold), and that the owner's Kiler then replaces and deletes the file of the admin.
- (M2.11) an ordinary user cannot rename the folders above `data\`, the limit under the decisions of [tasks/M2.md](tasks/M2.md); else IT puts `data\` at the root of a share.
- (M2.11a) `HoldOrCreateFolder` on a folder that is there: a file server may ask for the right to make a folder in the one above it. `FileSystemTests.HoldOrCreateFolder_AFolderThatExists_IsHeldAsItIs` passed on the shared folder with full control. The layer holds a folder that is there with `HoldFolder` first (`HoldBelow`), so a refusal reaches only a write that meets a folder made between its hold and its create.

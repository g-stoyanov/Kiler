# Checks in the real environment

The checkpoint right after M1: the checks of design section 14, now with the real `Kiler.exe` instead of the probe of M1.0b. The results of the probe are in the notes under M1.0b in [tasks/M1.md](tasks/M1.md); these checks confirm them and add what the probe could not show.

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

The subfolder is held open until its files are deleted, so during the run (one or two minutes on the shared folder) no one can rename or delete it or a folder above it.

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

A summary without internal names, hosts, paths or accounts goes into [tasks/M1.md](tasks/M1.md), as the results of M1.0b did, and into the course notes.

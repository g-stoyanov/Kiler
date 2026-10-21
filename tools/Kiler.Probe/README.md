# Kiler.Probe

A throwaway exe for the checks in design section 14 (task M1.0b). It is published the same way as `Kiler.exe`, self-contained single-file for `win-x64`, because antivirus tools judge the kind of file. M1.11 moves its `--diag` logic into `Kiler.exe` and deletes this project.

It is not in the solution and has no tests.

## Build

```powershell
dotnet publish tools/Kiler.Probe -c Release
```

The exe is `tools/Kiler.Probe/bin/Release/net10.0/win-x64/publish/Kiler.Probe.exe`. Copy only the exe. The other files in that folder come from the Web SDK and are not needed: `web.config` and `aspnetcorev2_inprocess.dll` are for IIS, and `*.staticwebassets.endpoints.json` is the static web assets manifest.

Variants for comparison:

```powershell
# compressed single file
dotnet publish tools/Kiler.Probe -c Release -p:EnableCompressionInSingleFile=true -o tools/Kiler.Probe/bin/publish-compressed

# Native AOT: needs the Visual Studio C++ tools, and vswhere.exe on PATH
$env:Path = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;" + $env:Path
dotnet publish tools/Kiler.Probe -c Release -p:PublishAot=true -p:PublishSingleFile=false -o tools/Kiler.Probe/bin/publish-aot
```

## Run

- **Double click on a local disk** (no parameters), or **`--server`** anywhere: starts a server on `127.0.0.1` on a free port and opens the default browser on a "Probe OK" page. It stops by itself 5 s after the page loads, or after 2 minutes without a request. The console shows the times from the process start to `Main`, to listening and to the page load.
- **Start without parameters from a network share** (a double click, or a console): does what `Kiler.exe` will do, so that we see whether the antivirus allows it.
  1. It copies itself to `%LOCALAPPDATA%\Programs\Kiler.Probe` through a temporary file and starts the copy with `--self-update`.
  2. The copy replaces itself the way an update will: it writes `Kiler.Probe.exe.new` next to itself, renames its own running exe to `Kiler.Probe.old.exe`, moves the new one into place and starts it with `--server`. On a failure it moves the old exe back.
  3. The new exe opens the "Probe OK" page and deletes `Kiler.Probe.old.exe` before it stops, with a few attempts.

  Steps 2 and 3 work only in that profile folder. Each step saves its own report: the first one on the share, the others in the profile folder. Delete `%LOCALAPPDATA%\Programs\Kiler.Probe` afterwards.
- **`Kiler.Probe.exe --diag <folder>`**: creates a temporary subfolder in `<folder>` and in it:
  - writes 2000 small JSON files one by one;
  - checks the owner of the new subfolder and of a new file;
  - replaces one file by renaming a temporary file over it (design 5.4) and checks the owner again;
  - lists the folder with names and dates three times;
  - reads the files in quarters, one by one and 8, 16 and 32 at a time, and the first quarter again;
  - deletes only the files it created, then the empty subfolder.

Every mode prints a report to the console and saves it next to the exe as `Kiler.Probe-report-<time>-<hex>.txt`. In the saved file the folders and the user name are shown as `<test folder>`, `<exe folder>`, `<profile>` and `<user>`, because the file may stay on a share that others read. Delete the reports from the share once they are read.

Exit codes:

- 0: every check passed;
- 1: a check failed, or the run was stopped with Ctrl+C;
- 2: wrong parameters, or the folder cannot be used.

The first Ctrl+C stops the run and cleans up. A second one ends the process at once and leaves the temporary subfolder `kiler-probe-<time>-<hex>`. If the report shows `[FAIL] Temporary subfolder removed`, delete that subfolder by hand.

On a local disk, a run as administrator can show the Administrators group as the owner instead of the user.

## Manual checks

On the shared folder, right click → **Properties**:

1. **Previous Versions** tab: are there automatic copies, and how often?
2. **DFS** tab: is the path DFS? Are there several referral targets (servers)? Several targets can mean replication between servers, and with it delays or conflicts between users on different targets.
3. **Security** tab → **Advanced**: who owns the folder and who manages the permissions (almost certainly IT)? Can ordinary users change files that other users created? Can they replace the exe? Anyone who can replace `Kiler.exe` can capture the passwords of everyone who starts it.

And the start itself:

4. In Explorer, double click `Kiler.Probe.exe` in the shared folder. Is there a SmartScreen or "Open File – Security Warning" dialog, an AppLocker message or an antivirus alert? Does the "Probe OK" page open?
5. The same from a local folder.

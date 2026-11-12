# Releases

How a new version of Kiler reaches the team, and where the key that signs it is kept. The decisions are in [tasks/M3.md](tasks/M3.md#decisions-for-the-module) (step 11 of PLAN M3, the signed updates).

## How it works

The shared folder holds `Kiler.exe` and `version.json` next to it:

```json
{
  "version": "1.0.1",
  "sha256": "<the SHA-256 of Kiler.exe, lowercase hex>",
  "signatures": [
    "<base64>"
  ]
}
```

Everyone runs Kiler from a copy of their own in `%LOCALAPPDATA%\Programs\Kiler`. At each start the copy reads `version.json` of the shared folder, in the background once the page is open, so the start never waits for the shared folder. It takes a new `Kiler.exe` only when the version is higher than its own, and a signature over that version and the SHA-256 of the copied file verifies with the public key built into the copy. The new version goes into place without a restart and runs from the next start, which deletes the old exe, kept until then as `Kiler.exe.old`. Anyone can write the shared folder, but no one can make that signature without the signing key, so a changed `Kiler.exe` or an older one under a higher number is refused. The first start from the shared folder checks its copy against `version.json` in the same way, with the version of the exe that runs in place of a higher one, and copies nothing without `version.json` (M3.8). From the first line, every start forbids DLLs from a network path (`NoRemoteImages`), so a DLL put next to `Kiler.exe` in the shared folder does not load after that, and then loads from System32 the few system DLLs that a planted copy of the same name could otherwise stop. Before that line Windows loads the system DLLs that the exe imports and what they need: the three whose needs Windows took from the folder of the exe there, `NTASN1.dll` and `SspiCli.dll` (M3.8), the exe now loads after that line (M3.8b). This was measured on the developer machine: with copies of 29 system DLLs that a start loads or may load next to `Kiler.exe`, none loaded from there, and `--diag` and the check of the first copy worked. What a system DLL needs can change with the version of Windows, so the trial use checks it on the company's Windows ([real-environment-checks.md](real-environment-checks.md#the-updates)).

The `sha256` of the file is there for people: the copies hash the file they copied, never the text of `version.json`. `signatures` holds one signature, and two during a change of the signing key (below); a copy takes the release when one of them verifies with its key.

## The signing key

A key pair of ECDSA P-256, made once by the maintainer with the release tool, `tools/Kiler.Release`. The public key is the constant `UpdateKey.PublicKey` in Kiler.App. The private key is only in the key file, locked with its password.

- **The key file** ends with `.kilerkey` in lower case, for example `Kiler-signing.kilerkey`, which `.gitignore` keeps out of git. It is one line of text. Alone it is of no use: the key in it is locked with the password. With the password, anyone can sign a `Kiler.exe` that every copy takes.
- **The password** is made by the password manager: long and random, at least 16 characters, as the tool requires. Once the file is copied, nothing but the password and PBKDF2 slows down a guess, so a password a person makes up is not enough.
- **Where they are kept:** the file on the maintainer's machine, and the file with its password in one entry of the company password manager. That entry is the signing key itself: only the maintainer can read it, never the team that writes the shared folder.
- **Never** in the repo, the shared folder, a mail, a chat, CI or a log. The tool never prints the password, and asks for it without showing it.
- **Lost key:** the copies take no new version signed with another key. Every user then installs the next version by hand: compare `Get-FileHash` of `Kiler.exe` on the shared folder with the announced SHA-256 (step 5 below), then delete `%LOCALAPPDATA%\Programs\Kiler` and start Kiler from the shared folder. The hash check is the only defense there, as a start from the shared folder runs whatever exe is there, and it covers only the exe: check too that no `.dll` is next to it, as "How it works" rests on one measurement until the trial use. That is why the password manager keeps a copy.
- **A key that has leaked** (the file and its password): it can no longer vouch for a release, as anyone with it can sign too, and it is never used again, not even next to a new key. A copy that has only it may already run an exe that someone else signed, so only an install by hand, checked against the announced SHA-256, puts it right. Make a new key ("Make the key" below), and every user installs the release with the new public key by hand, as for a lost key.
- **A new key while the old one is safe** (for example a new maintainer): the copies change over by themselves, as each release is signed with the old key and the new one for a while ("Change the key" below).

### Make the key

Once, in a terminal on the maintainer's machine, from the repository root, into a folder outside the repo that no cloud service syncs:

```powershell
dotnet run --project tools\Kiler.Release -- keygen "$HOME\Kiler-signing.kilerkey"
```

It asks for the password twice, writes the file (never over a file that is there) and prints `Public key: <base64>`. Then:

1. Put the file and its password into one entry of the password manager.
2. Put the public key into `UpdateKey.PublicKey` in `src/Kiler.App/UpdateKey.cs`. It is public, so it goes into the repo.

### Change the key

Only while the old key is safe; a key that has leaked is replaced as above. A copy takes a release when one of the signatures of `version.json` verifies with the key built into it, so for a while each release carries a signature of the old key and one of the new (decided in M3.6 and M3.8a, [tasks/M3.md](tasks/M3.md#decisions-for-the-module)).

1. Make the new key with `keygen` into a file of another name, for example `Kiler-signing-2.kilerkey`, and put the file and its password into a new entry of the password manager ("Make the key" above). Keep the old key as it is.
2. Put the new public key into `UpdateKey.PublicKey` in place of the old one, and make a release ("Make a release" below), signed with both keys, the old key first:

   ```powershell
   dotnet run --project tools\Kiler.Release -- sign artifacts\publish\Kiler.exe "$HOME\Kiler-signing.kilerkey" "$HOME\Kiler-signing-2.kilerkey"
   ```

   It asks for the password of each key file, naming the file, and prints the public key of each in the order of the files. It refuses the same key twice. Before the release goes to the shared folder, check that the second public key is the one in `UpdateKey.PublicKey` of the commit that was published, and the first the one it replaces: the tool cannot. An exe that still carried the old key would be taken by every copy with the first signature, and every copy would go on trusting only the old key; step 3 would not show it, as the foot line shows only the version, and step 4 would leave every copy to an install by hand. The copies with the old key take this release with the first signature and run with the new key from their next start; a first start from the shared folder takes it with the second.
3. Sign every release so, with both keys, until each user's Kiler shows in its foot line the version of the first release with the new key, or a higher one. Ask the team: a copy takes a new version in the background and runs it from the next start, so someone may have to start Kiler twice.
4. Then retire the old key: sign with the new key alone, and destroy every copy of the old key. Delete its file for good (`Remove-Item` or Shift+Delete, not into the Recycle Bin) and its entry in the password manager, with the trash and the earlier versions of the entry there; in a handover, at both maintainers; and in any backup that holds it. A copy that still has only the old key takes whatever that key signs, and nothing else needs it any more.

A copy that missed every release with both keys, such as that of someone who did not start Kiler for that whole time, refuses every later release, as no signature verifies with its key: it says so in a line and keeps running its version, which its foot line shows. Its user installs the current release by hand, as for a lost key.

## Make a release

1. Raise `VersionPrefix` in `Directory.Build.props` (`major.minor.patch`, higher than the last release: a copy takes only a higher version). Commit and push; CI passes.
2. Publish and test the exe:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\publish.ps1
   powershell -ExecutionPolicy Bypass -File .\smoke.ps1
   powershell -ExecutionPolicy Bypass -File .\e2e.ps1
   ```

3. Sign it:

   ```powershell
   dotnet run --project tools\Kiler.Release -- sign artifacts\publish\Kiler.exe "$HOME\Kiler-signing.kilerkey"
   ```

   It reads the version from the version resource of the exe and its SHA-256 from the same read, asks for the password of the key file, checks the new signature, writes `artifacts\publish\version.json` and prints the version, the SHA-256 and the public key. Check that the public key is the one in `UpdateKey.PublicKey`: the tool cannot, and a release signed with another key is refused by every copy. During a change of key, a second key file after the first signs with both ("Change the key" above). The exe and the key file must be on a local disk: the tool refuses a path on the network, so an exe on the shared folder, which anyone in the team may have changed, is never signed where it is.
4. Copy `Kiler.exe` to the shared folder first, then `version.json`. Until `version.json` is there, the copies see the old version and take nothing, and a first start from the shared folder refuses to copy itself, as the two versions differ; it works again once both are there. A build that is not signed, such as one for a check, makes no first copy from a network folder either.
5. Announce the version and the SHA-256 that `sign` printed outside the shared folder, for example in the team chat, so a start from the shared folder can be checked by hand. A `.dll` next to `Kiler.exe` there has no business there, whether or not it would load ("How it works"): delete it, and find out who put it there and what else they changed. A person compares `Get-FileHash` of `Kiler.exe` on the shared folder with the announcement, never with `version.json`: whoever can change the exe there can change `version.json` too. `Get-FileHash` shows the hash in upper case and `sign` in lower case, so compare them without regard to case.

   ```powershell
   Get-FileHash '\\server\share\Kiler\Kiler.exe'
   ```

## The release tool

`dotnet run --project tools\Kiler.Release -- --help` shows its commands. It ends with exit code 0 when it did the work, 1 when it refused (nothing is written then), and 2 for wrong arguments.

The real password is only typed, in PowerShell or Windows Terminal, where it is not shown. The tool refuses an input that is not a console, such as a pipe or the terminal of Git Bash, where a typed password would be shown and kept. `--password-stdin` at the end of the command takes the password from a line of the input instead: it is for the tests and for a key made only for a check, never for the real password, which would stay in the history of the shell or in a script.

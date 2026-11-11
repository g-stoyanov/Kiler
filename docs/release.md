# Releases

How a new version of Kiler reaches the team, and where the key that signs it is kept. The decisions are in [tasks/M3.md](tasks/M3.md#decisions-for-the-module) (step 11 of PLAN M3, the signed updates).

**Status:** the release tool and the public key in the exe are done (M3.5). The copies check and take a new version from M3.6 to M3.8 on; until then a release is put in place by hand.

## How it works

The shared folder holds `Kiler.exe` and `version.json` next to it:

```json
{
  "version": "1.0.1",
  "sha256": "<the SHA-256 of Kiler.exe, lowercase hex>",
  "signature": "<base64>"
}
```

Everyone runs Kiler from a copy of their own in `%LOCALAPPDATA%\Programs\Kiler`. At each start the copy reads `version.json` of the shared folder. It takes a new `Kiler.exe` only when the version is higher than its own, and the signature over that version and the SHA-256 of the copied file verifies with the public key built into the copy. Anyone can write the shared folder, but no one can make that signature without the signing key, so a changed `Kiler.exe` or an older one under a higher number is refused. The first start from the shared folder checks its copy against `version.json` in the same way.

The `sha256` of the file is there for people: the copies hash the file they copied, never the text of `version.json`.

## The signing key

A key pair of ECDSA P-256, made once by the maintainer with the release tool, `tools/Kiler.Release`. The public key is the constant `UpdateKey.PublicKey` in Kiler.App. The private key is only in the key file, locked with its password.

- **The key file** ends with `.kilerkey` in lower case, for example `Kiler-signing.kilerkey`, which `.gitignore` keeps out of git. It is one line of text. Alone it is of no use: the key in it is locked with the password. With the password, anyone can sign a `Kiler.exe` that every copy takes.
- **The password** is made by the password manager: long and random, at least 16 characters, as the tool requires. Once the file is copied, nothing but the password and PBKDF2 slows down a guess, so a password a person makes up is not enough.
- **Where they are kept:** the file on the maintainer's machine, and the file with its password in one entry of the company password manager. That entry is the signing key itself: only the maintainer can read it, never the team that writes the shared folder.
- **Never** in the repo, the shared folder, a mail, a chat, CI or a log. The tool never prints the password, and asks for it without showing it.
- **Lost key:** the copies take no new version signed with another key. Every user then installs the next version by hand: compare `Get-FileHash` of `Kiler.exe` on the shared folder with the announced SHA-256 (step 5 below), then delete `%LOCALAPPDATA%\Programs\Kiler` and start Kiler from the shared folder. The hash check is the only defense there, as a start from the shared folder runs whatever exe is there. That is why the password manager keeps a copy.
- **A key that has leaked** (the file and its password): it can no longer vouch for a release, as anyone with it can sign too. Make a new key, and every user installs the release with the new public key by hand, as for a lost key.
- **A new key while the old one is safe** (for example a new maintainer) is not possible yet. `version.json` carries one signature, and each exe checks it with its own key: while the release with the new key is on the shared folder, a first start refuses it, and a copy that misses that release refuses every later one. How `version.json` carries a change of key is decided in M3.6 ([tasks/M3.md](tasks/M3.md)); until then, a new key is installed by hand, as for a lost key.

### Make the key

Once, in a terminal on the maintainer's machine, from the repository root, into a folder outside the repo that no cloud service syncs:

```powershell
dotnet run --project tools\Kiler.Release -- keygen "$HOME\Kiler-signing.kilerkey"
```

It asks for the password twice, writes the file (never over a file that is there) and prints `Public key: <base64>`. Then:

1. Put the file and its password into one entry of the password manager.
2. Put the public key into `UpdateKey.PublicKey` in `src/Kiler.App/UpdateKey.cs`. It is public, so it goes into the repo.

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

   It reads the version from the version resource of the exe and its SHA-256 from the same read, asks for the password, checks the new signature, writes `artifacts\publish\version.json` and prints the version, the SHA-256 and the public key. Check that the public key is the one in `UpdateKey.PublicKey`: the tool cannot, and a release signed with another key is refused by every copy. The exe and the key file must be on a local disk: the tool refuses a path on the network, so an exe on the shared folder, which anyone in the team may have changed, is never signed where it is.
4. Copy `Kiler.exe` to the shared folder first, then `version.json`. Until `version.json` is there, the copies see the old version and take nothing.
5. Announce the version and the SHA-256 that `sign` printed outside the shared folder, for example in the team chat, so a start from the shared folder can be checked by hand. A person compares `Get-FileHash` of `Kiler.exe` on the shared folder with the announcement, never with `version.json`: whoever can change the exe there can change `version.json` too. `Get-FileHash` shows the hash in upper case and `sign` in lower case, so compare them without regard to case.

   ```powershell
   Get-FileHash '\\server\share\Kiler\Kiler.exe'
   ```

## The release tool

`dotnet run --project tools\Kiler.Release -- --help` shows its commands. It ends with exit code 0 when it did the work, 1 when it refused (nothing is written then), and 2 for wrong arguments.

The real password is only typed, in PowerShell or Windows Terminal, where it is not shown. The tool refuses an input that is not a console, such as a pipe or the terminal of Git Bash, where a typed password would be shown and kept. `--password-stdin` at the end of the command takes the password from a line of the input instead: it is for the tests and for a key made only for a check, never for the real password, which would stay in the history of the shell or in a script.

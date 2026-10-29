---
name: kiler-dotnet
description: Kiler's rules for C# and .NET code. Use when writing or changing C# code, .csproj files or .NET tests.
---

# Kiler: C# and .NET

Decisions come from `docs/PLAN.md` (sections 2 and 3) and the task files `docs/tasks/M<n>.md`. Anything not decided yet is listed under "Deferred" with the module where it gets decided. Do not invent it; if a task needs such a decision, ask.

## Area references

The rules of one area are in a file of their own beside this one. Read the file of the area that the task changes, and only that one:

- [host.md](host.md): Kiler.App and its tests: the host, the access to the server, the single instance, the heartbeat, the local copy of the exe, the UI files and the publish, the smoke tests and `--diag` (M1); the data folder, its local copy and the watch in the host (M2.10).
- [storage.md](storage.md): Kiler.Storage and its tests, and code that calls the layer: the data layer (M2).

A rule for all the code stays here. A rule of one area goes into the file of that area; a new area, such as Kiler.Crypto or Kiler.Core, gets a file of its own when its first task needs one. Each rule is written once: a task note points to the rule instead of repeating it.

## Projects and dependencies

```
Kiler.App ──► Kiler.Core ──► Kiler.Crypto
                   │
                   └───────► Kiler.Storage
```

- **Kiler.Crypto:** cryptography. No files, no network, no dependency on the other projects.
- **Kiler.Storage:** the JSON files in `data\`: atomic writes, history, trash, cache, change polling. It knows nothing about encryption; encrypted parts are plain strings to it. It knows only `id` and `version` of a document (see [storage.md](storage.md)).
- **Kiler.Core:** templates, records, search, users, permissions, audit. It connects Crypto and Storage.
- **Kiler.App:** `Kiler.exe`: the host, the API and the Windows implementations.
- Records, users and settings in `data\` are written only by Kiler.Storage. The exception is the temporary subfolder of `--diag` (M1.11).
- NuGet package versions live only in `Directory.Packages.props`. Shared settings live in `Directory.Build.props`.

## Native AOT

The exe is published with Native AOT, decided after the checks in the real environment (M1.0b). Everything must work under AOT:

- JSON only with source generation (`JsonSerializerContext`). No reflection-based serialization.
- No `Activator.CreateInstance`, no `Type.GetType` from a string, no dynamic assembly loading, no `dynamic`, no `typeof(T).GUID`.
- COM only through the COM source generator: `[GeneratedComInterface]`, objects wrapped by `UniqueComInterfaceMarshaller<T>` and let go with `ComObject.FinalRelease()`. Native AOT has no built-in COM, so no `[ComImport]` and no `Marshal.ReleaseComObject`. IIDs and CLSIDs are `Guid` constants. P/Invoke is `[LibraryImport]`, so Kiler.App has `AllowUnsafeBlocks`.
- Minimal APIs. No MVC, no Razor.
- The trim and AOT analyzers are on and warnings are errors. Do not suppress them without a comment that explains why. The test projects turn them off (`tests/Directory.Build.props`), because they are not published.
- Every type that the API reads or writes as JSON goes into `KilerJsonContext` (Kiler.App). `Kiler.App.Tests` runs with reflection-based JSON turned off (`JsonSerializerIsReflectionEnabledByDefault`), as Native AOT does, so a missing type fails a test.

## Local folder

- `%LOCALAPPDATA%\Kiler` holds the instance file with the token, `share.json` and, from M2, the local copy of the data. `PrivateFolder.Create` makes it with a protected ACL: the user and SYSTEM, nothing inherited. A folder that exists is left as it is.
- Everything in it is written through `PrivateFolder.WriteFile` (a temporary file and a rename), or into a folder made by `PrivateFolder.Create`.
- The folders of the profile come from `UserFolders` (the known folders of Windows), not from environment variables.
- Kiler.App and Kiler.App.Tests carry `[assembly: SupportedOSPlatform("windows")]` from their project files, so the platform analyzer accepts Windows APIs there without guards. The other projects stay platform-neutral; the one exception is `NativeFiles` in Kiler.Storage, behind guards (M2.3, see [storage.md](storage.md)).

## Style

The build enforces `.editorconfig` (M1.1): file-scoped namespaces, usings outside the namespace and no unnecessary ones, braces, explicit accessibility, readonly fields. Names: interfaces `IName`, private fields `_camelCase`, private constants and private static readonly fields `PascalCase`.

Files keep the line endings they have in the working tree: LF for almost all, CRLF for the few that the .NET tools wrote (`.gitignore`, `Kiler.slnx`, `dotnet-tools.json`). A script that rewrites a file keeps them; Python on Windows writes CRLF in text mode (M1.6). Check them by the bytes: `grep` in Git Bash can show a wrong count. Source files have no BOM: Windows PowerShell 5.1 adds one with `Set-Content -Encoding utf8` and `Out-File`, so a PowerShell script that rewrites a file uses `[IO.File]::WriteAllText` with `New-Object Text.UTF8Encoding $false` (M2.7d).

The rest are general good practices, not project decisions. If a task asks for something else, the task wins.

- Nullable is on. Use `!` only with a reason.
- Async all the way, with `CancellationToken`. No `.Result` or `.Wait()`.
- Time comes from `TimeProvider`, not `DateTime.Now`, so tests can use `FakeTimeProvider`.
- Dispose `IDisposable` resources with `using`.
- Passwords, keys, tokens and decrypted values never go into logs, exception messages or `ToString()`. A record prints all its properties, computed ones too, so a record that holds one overrides `ToString()`.

## Windows parts

Per PLAN section 3, the current Windows account, DPAPI and opening Explorer are interfaces in Kiler.Core, implemented in Kiler.App. Their names are chosen when they are first needed. Tests use fake implementations.

The file owner is the exception (decided at the start of M2): an interface in Kiler.Storage (`IFileOwner`, M2.6), implemented in Kiler.App by `FileOwner.Instance`. Storage reads the owner through the handle it reads the file with and keeps it in the local copy (M2.7), so a start from the local copy does not ask the share for every owner.

Opening the browser is `IBrowser` in Kiler.App (decided in M1.5), because only the start of the exe opens a browser. Tests use a fake, and no test starts the real browser.

## Tests

- **xUnit v3** (package `xunit.v3`) with the built-in `Assert`. No FluentAssertions: since v8 its license is paid for commercial use.
- **Microsoft Testing Platform.** xunit.v3 4.x runs on MTP v2, and `global.json` sets it as the `dotnet test` runner. Do not add the VSTest packages (`Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`, `coverlet.collector`); coverage uses `coverlet.MTP`.
- One test project per project in `src/`. `tests/Directory.Build.props` gives them `xunit.v3`, `OutputType` Exe and a global `using Xunit`.
- API tests run on the real Kestrel through `KilerServer` in `Kiler.App.Tests`. It starts `KilerHost` on a free port of 127.0.0.1 and gives clients with or without the cookie. WebApplicationFactory is not used (decided in M1.4): its test server has no port, so the Host check rejects it, and it runs `Main`, which starts real things from M1.5 on (mutex, `instance.json`, browser).
- **TDD in Kiler.Crypto and Kiler.Storage:** write the test first, run it and see it fail, then write the code.
- A test name states the scenario and the expected result.
- File tests work in a temporary folder that is deleted after the test. Kiler.Storage.Tests has its own rules for it (storage.md).
- Tests do not use the external network and do not depend on run order. The local server on loopback is allowed; API and smoke tests use it.
- Test data is made up. No real system names, hosts, accounts or passwords. The example names of the design can be terms of `data-guard`, so do not copy them; take names such as `demo.user` (found at the start of M2).
- An assertion prints what it compares, and the message of a failed test goes into the public CI log. A token, a password, a key or a decrypted value is compared with `Assert.True` and a message without it, or masked first; `Assert.Equal`, `Assert.Single` and `Assert.Contains` print the values or the whole collection (M1.9).
- xUnit v3 treats a run with zero tests as a failure.
- A new test is checked to fail against broken code: break or take out what it guards, see it fail, put the code back. A broken version that does not build proves nothing, because the tests then run against the old binaries (M1.3). Taking out the last use of a `using` breaks the build with IDE0005, so change an argument instead of taking out the call (M2.2).
- The Write and Edit tools turn a JSON or C# escape of a backslash, `u` and four hex digits in their text into the character, in code, in comments and in Markdown alike; only a lone surrogate stays as it is. A test that needs such an escape in its JSON builds it in code (`DocumentFormatTests.Escaped`), and the bytes of a file with one are checked afterwards (M2.2).
- A Python edit script given through a Bash heredoc turned the `\\?\` of a C# string into `\?\` (M2.3), and the `\r` of `trash\records` in a comment into a carriage return (M2.7a): in a Python string literal a backslash starts an escape. Edit text with backslashes with the Edit tool, and check the result.
- A test that a guard is proven by can pass through another path of the code: in M2.7a four of the new tests still passed with their guard broken (a move made earlier through another rule, a stamp that changed anyway). Break each guard and run only the test class whose test is meant to fail for it (`--filter-class`), as the question is whether that test fails; rework the test that does not fail. The whole test project runs only to find which other test catches a guard whose own test passes (decided by the user after M2.9b, where every break ran the whole project).
- `CA1416` does not take `Assert.Skip` for the end of a method, so in a test the Windows calls stand inside `if (OperatingSystem.IsWindows())`, with the skip in the `else` (M2.5).
- A guard is broken for that check with a condition the compiler cannot fold (`if (x.Count < 0)`, not `if (false)`, which is CS0162, an error here). A script that puts the code back with `Copy-Item` must set the write time of the file to now: `Copy-Item` keeps the old one, and the incremental build then keeps the binary of the broken code, so the next runs test it (M2.7). A script that makes two breaks in one file puts its saved copies back in the reverse order: in the order of the changes, the first break stays in the file for every later run (M2.7c). The script reports a break that does not build as such, as it proves nothing: a `lock (_gate)` is taken out with `while (true)` in its place, which still returns from inside, not with an `if` (CS0161), and a check of a non-nullable value against `null` trips the nullable analysis (M2.9).
- A script that breaks the guards one by one in the background is read when it ends, not watched: a `tail -f` of its results file (a Monitor) held the file, and every later `Add-Content` of the script failed, though the runs went on and the code was put back (M2.8b).
- An age check is broken by taking it out, not by setting the age to zero: a `FakeTimeProvider` that starts at the real time is behind the times of the files made after it, so a file made a moment ago still counts as young (M2.7e).
- A test class with a field named `Record`, such as a `DocumentKey`, hides `Xunit.Record`: write `Xunit.Record.Exception` there.
- A null-conditional call skips the evaluation of its arguments too: `outside?.AddRange(copy.Load().Outside)` does not load at all when `outside` is null (M2.8c). Make the call with a side effect first, then pass its result.
- Tests on the real clock, such as the stop by the heartbeat: take the time stamp before the action that starts the wait, check a lower and an upper bound, and leave margins for a slow CI machine (`windows-latest`) (M1.6).

## Manual checks

- Publish with `powershell -ExecutionPolicy Bypass -File .\publish.ps1` (M1.8), then run the smoke tests with `powershell -ExecutionPolicy Bypass -File .\smoke.ps1` (M1.9). Native AOT needs the Visual Studio C++ tools and `vswhere.exe`; the script puts the Visual Studio Installer folder on PATH when `vswhere.exe` is not there (M1.0b, M1.3). `artifacts\` is out of git.
- The UI of the published exe is checked in Chrome or Edge, not in the Claude Code in-app browser, which blocks the network requests of a SharedWorker and so shows "Stopped" (kiler-ui, Manual checks).
- Kiler.exe started by hand or from a script runs with `--data <temporary folder>`, so it does not meet the user's own Kiler, and from a local folder, so it does not copy itself into the profile (M1.5a). Afterwards, check that no `instance-*.json` was left in `%LOCALAPPDATA%\Kiler`, and delete the local copy of the temporary data folder, `%LOCALAPPDATA%\Kiler\copies\<key>` (the folder that `Kiler opens the data folder …` names, M2.10).
- The `Open:` line holds the token. A script that reads it masks the token in everything it prints.
- Requests from the shell to the local server must not go through the corporate proxy. In the Claude process `NO_PROXY` covers 127.0.0.1 and localhost. With curl's `--noproxy`, quote the `*`, or bash expands it to file names (M1.4).

## Deferred

Nothing at the end of M1; the build and test commands are in CLAUDE.md, section "Code" (M1.12). Open questions go here with the module that decides them; the current ones are in PLAN section 6.

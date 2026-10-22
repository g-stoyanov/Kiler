---
name: kiler-dotnet
description: Kiler's rules for C# and .NET code. Use when writing or changing C# code, .csproj files or .NET tests.
---

# Kiler: C# and .NET

Decisions come from `docs/PLAN.md` (sections 2 and 3) and `docs/tasks/M1.md`. Anything not decided yet is listed under "Deferred" with the module where it gets decided. Do not invent it; if a task needs such a decision, ask.

## Projects and dependencies

```
Kiler.App ──► Kiler.Core ──► Kiler.Crypto
                   │
                   └───────► Kiler.Storage
```

- **Kiler.Crypto:** cryptography. No files, no network, no dependency on the other projects.
- **Kiler.Storage:** the JSON files in `data\`: atomic writes, history, trash, cache, change polling. It knows nothing about encryption; encrypted parts are plain strings to it.
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

## Host (M1.3)

- `KilerHost.Build` uses `WebApplication.CreateEmptyBuilder` with `UseKestrelCore` and listens on `IPAddress.Loopback` only, set in code. The empty builder reads no arguments, environment variables or config files. Keep it that way: what tests need to change (port, token, timers, data folder) comes through `StartOptions` or DI, not configuration.
- The Kiler.App types are `internal`, and the tests reach them through `InternalsVisibleTo`. `Program` is public because .NET 10 generates it so for top-level statements.

## Access to the server (M1.4)

- `AccessGuard` runs in front of every request, in this order:
  - `Host` must be `127.0.0.1:<port>` or `localhost:<port>`, with the port of the connection (400);
  - `Sec-Fetch-Site`, when present, must be `same-origin` or `none` (403). Other ports of 127.0.0.1 are the same site for the browser, so `SameSite=Strict` does not stop their requests;
  - `GET /?t=<token>` sets the cookie and redirects to `/`;
  - every other request needs the cookie (401).
- No endpoint and no file is exempt, `/api/health` included. A new endpoint needs nothing extra, because the guard runs before it. Do not loosen the guard for tests.
- `KilerHost.Build` adds the guard first and calls `UseRouting()` after it. Keep that order, and add static files (M1.8) and any other middleware after the guard. Otherwise WebApplication puts routing first, and an endpoint with `ShortCircuit()` or `MapShortCircuit` runs before the guard.
- `AccessToken` is made in `KilerHost.Build` and reaches other code through DI. It never goes into the command line, the environment, logs, exception messages, `ToString()` or the console. It is compared only with `AccessToken.Matches`, which is constant-time. The exceptions, all from M1.5:
  - the address with the token is handed to the default browser through the shell, so it lands on the browser's command line. The shell offers no other way;
  - the address is printed as `Open: <address>` with `--no-browser`, and when the browser does not open;
  - the instance file holds it for the next start (see "Single instance").
- A token read from outside the process, such as the instance file, goes through `AccessToken.TryParse`, which accepts only the exact 43-character form.

## Single instance (M1.5)

- One Kiler per user, session and data folder. `InstanceMutex` is a named mutex with `NamedWaitHandleOptions` `CurrentUserOnly` and `CurrentSessionOnly`. The file is `%LOCALAPPDATA%\Kiler\instance-<session>.json`, and with `--data` `instance-<session>-<key>.json`, the key from the full path of the folder (`InstancePlace`). The mutex and the file have the same scope. Keep it that way.
- The mutex is held for the life of the process, on a thread of its own. A mutex belongs to the thread that took it and is released when that thread ends, and after an `await` the code runs on thread pool threads, which end when idle.
- `SingleInstance` decides. The file is read only while another process holds the mutex. When this process gets the mutex, a file left over is deleted before the server starts, and its port is never contacted; if it cannot be deleted, Kiler does not start.
- The file is written after the server listens, through a temporary file and a rename, and deleted on `ApplicationStopping`, before Kestrel lets the port go. Readers open it with `FileShare.ReadWrite | FileShare.Delete`, so they never block that delete.
- The second start sends the cookie to `http://127.0.0.1:<port>/api/health` without a proxy and without following redirects, then opens `AccessGuard.EntryAddress`. Always 127.0.0.1, never `localhost`.
- `DefaultBrowser` hands the shell only an `http` address on 127.0.0.1. The shell starts whatever it gets, a program too.

## Local copy (M1.5a)

- Started from a network folder (a UNC path or a mapped network drive), `LocalCopy` copies the exe to `%LOCALAPPDATA%\Programs\Kiler` when there is no copy, writes the folder to `%LOCALAPPDATA%\Kiler\share.json`, makes `Kiler.lnk` in the Start menu when it is missing, starts the copy and exits. The exe in the profile, under any name, never copies itself.
- The single instance comes first. The lock is let go before the copy starts, and the copy takes it.
- An existing copy is never replaced: only the copy checks a new version (M3). The copy goes through a temporary file and a rename that does not overwrite.
- The copy starts without the shell, in its own folder, with `StartOptions.ToServerArguments()`: `--data` as a full path, because the instance names come from it. Its console is inherited and not redirected, so nothing reads its output, which can hold the `Open:` line.
- A failed copy or start ends with exit code 1. Kiler never runs from the shared folder instead. A failed shortcut is only a warning.
- `--help` and `--diag` run where they are and copy nothing.
- Every start from the shared folder runs the exe there first, and anyone can replace it. So the start says when the shared folder changes, and that the next starts go through the Start menu.
- `ICopyFiles`, `IShortcuts` and `IProcessStarter` live in Kiler.App, as `IBrowser` does: only the start of the exe uses them.

## Local folder

- `%LOCALAPPDATA%\Kiler` holds the instance file with the token, `share.json` and, from M2, the local copy of the data. `PrivateFolder.Create` makes it with a protected ACL: the user and SYSTEM, nothing inherited. A folder that exists is left as it is.
- Everything in it is written through `PrivateFolder.WriteFile` (a temporary file and a rename), or into a folder made by `PrivateFolder.Create`.
- The folders of the profile come from `UserFolders` (the known folders of Windows), not from environment variables.
- Kiler.App and Kiler.App.Tests carry `[assembly: SupportedOSPlatform("windows")]` from their project files, so the platform analyzer accepts Windows APIs there without guards. The other projects stay platform-neutral.

## Style

The build enforces `.editorconfig` (M1.1): file-scoped namespaces, usings outside the namespace and no unnecessary ones, braces, explicit accessibility, readonly fields. Names: interfaces `IName`, private fields `_camelCase`, private constants and private static readonly fields `PascalCase`.

The rest are general good practices, not project decisions. If a task asks for something else, the task wins.

- Nullable is on. Use `!` only with a reason.
- Async all the way, with `CancellationToken`. No `.Result` or `.Wait()`.
- Time comes from `TimeProvider`, not `DateTime.Now`, so tests can use `FakeTimeProvider`.
- Dispose `IDisposable` resources with `using`.
- Passwords, keys, tokens and decrypted values never go into logs, exception messages or `ToString()`. A record prints all its properties, computed ones too, so a record that holds one overrides `ToString()`.

## Windows parts

Per PLAN section 3, the current Windows account, DPAPI, the file owner and opening Explorer are interfaces in Kiler.Core, implemented in Kiler.App. Their names are chosen when they are first needed. Tests use fake implementations.

Opening the browser is `IBrowser` in Kiler.App (decided in M1.5), because only the start of the exe opens a browser. Tests use a fake, and no test starts the real browser.

## Tests

- **xUnit v3** (package `xunit.v3`) with the built-in `Assert`. No FluentAssertions: since v8 its license is paid for commercial use.
- **Microsoft Testing Platform.** xunit.v3 4.x runs on MTP v2, and `global.json` sets it as the `dotnet test` runner. Do not add the VSTest packages (`Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`, `coverlet.collector`); coverage uses `coverlet.MTP`.
- One test project per project in `src/`. `tests/Directory.Build.props` gives them `xunit.v3`, `OutputType` Exe and a global `using Xunit`.
- API tests run on the real Kestrel through `KilerServer` in `Kiler.App.Tests`. It starts `KilerHost` on a free port of 127.0.0.1 and gives clients with or without the cookie. WebApplicationFactory is not used (decided in M1.4): its test server has no port, so the Host check rejects it, and it runs `Main`, which starts real things from M1.5 on (mutex, `instance.json`, browser).
- **TDD in Kiler.Crypto and Kiler.Storage:** write the test first, run it and see it fail, then write the code.
- A test name states the scenario and the expected result.
- File tests work in a temporary folder that is deleted after the test.
- Tests do not use the external network and do not depend on run order. The local server on loopback is allowed; API and smoke tests use it.
- Test data is made up. No real system names, hosts, accounts or passwords.
- xUnit v3 treats a run with zero tests as a failure.

## Deferred

| Question | When |
|---|---|
| Build and test commands | M1.12, in CLAUDE.md |

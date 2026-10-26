---
name: kiler-dotnet
description: Kiler's rules for C# and .NET code. Use when writing or changing C# code, .csproj files or .NET tests.
---

# Kiler: C# and .NET

Decisions come from `docs/PLAN.md` (sections 2 and 3) and the task files `docs/tasks/M<n>.md`. Anything not decided yet is listed under "Deferred" with the module where it gets decided. Do not invent it; if a task needs such a decision, ask.

## Projects and dependencies

```
Kiler.App ──► Kiler.Core ──► Kiler.Crypto
                   │
                   └───────► Kiler.Storage
```

- **Kiler.Crypto:** cryptography. No files, no network, no dependency on the other projects.
- **Kiler.Storage:** the JSON files in `data\`: atomic writes, history, trash, cache, change polling. It knows nothing about encryption; encrypted parts are plain strings to it. It knows only `id` and `version` of a document (see "Data layer").
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
- The cookie is named per instance (M1.7), as the mutex and the file: `kiler`, and `kiler-<key>` with `--data` (`InstancePlace.CookieName`, `InstancePlace.CookieNameFor`). Cookies are not separated by port, so two Kilers that run at the same time, such as the dev mode and the user's Kiler, would otherwise overwrite each other's cookie. Not per port: the port changes with every start, and the browser keeps the old cookies, also across a restart that restores the session. `HealthCheck` takes the name of its instance.
- No endpoint and no file is exempt, `/api/health` included. A new endpoint needs nothing extra, because the guard runs before it. Do not loosen the guard for tests.
- `KilerHost.Build` adds the guard first and calls `UseRouting()` after it. Keep that order, and add the UI files (`UiFiles.Use`, M1.8) and any other middleware after the guard. Otherwise WebApplication puts routing first, and an endpoint with `ShortCircuit()` or `MapShortCircuit` runs before the guard.
- `AccessToken` is made in `KilerHost.Build` and reaches other code through DI. It never goes into the command line, the environment, logs, exception messages, `ToString()` or the console. It is compared only with `AccessToken.Matches`, which is constant-time. The exceptions, all from M1.5:
  - the address with the token is handed to the default browser through the shell, so it lands on the browser's command line. The shell offers no other way;
  - the address is printed as `Open: <address>` with `--no-browser`, and when the browser does not open;
  - the instance file holds it for the next start (see "Single instance").
- A token read from outside the process, such as the instance file, goes through `AccessToken.TryParse`, which accepts only the exact 43-character form.

## Single instance (M1.5)

- One Kiler per user, session and data folder. `InstanceMutex` is a named mutex with `NamedWaitHandleOptions` `CurrentUserOnly` and `CurrentSessionOnly`. The file is `%LOCALAPPDATA%\Kiler\instance-<session>.json`, and with `--data` `instance-<session>-<key>.json`, the key from the full path of the folder (`InstancePlace`). The mutex and the file have the same scope. Keep it that way. The cookie name carries the same key, but not the session: one browser profile serves one session.
- The mutex is held for the life of the process, on a thread of its own. A mutex belongs to the thread that took it and is released when that thread ends, and after an `await` the code runs on thread pool threads, which end when idle.
- `SingleInstance` decides. The file is read only while another process holds the mutex. When this process gets the mutex, a file left over is deleted before the server starts, and its port is never contacted; if it cannot be deleted, Kiler does not start.
- The file is written after the server listens, through a temporary file and a rename, and deleted on `ApplicationStopping`, before Kestrel lets the port go. Readers open it with `FileShare.ReadWrite | FileShare.Delete`, so they never block that delete.
- The second start sends the cookie to `http://127.0.0.1:<port>/api/health` without a proxy and without following redirects, then opens `AccessGuard.EntryAddress`. Always 127.0.0.1, never `localhost`.
- `DefaultBrowser` hands the shell only an `http` address on 127.0.0.1. The shell starts whatever it gets, a program too.

## Heartbeat (M1.6)

- `Heartbeat` counts the open `/api/events` streams and stops the host with `StopApplication` after its waits (`HeartbeatTimes`): 60 s for the first page, 10 s after the last one closes. `--first-wait` and `--idle-wait` set the waits; the ping interval (15 s) is set only by tests in the process, through `StartOptions`.
- A connection is counted only after `AccessGuard`, and it is released through `HttpResponse.RegisterForDispose`, so also when the stream never starts. A count that leaks keeps Kiler running for ever.
- A response that stays open, such as the event stream, ends on `ApplicationStopping`: Kestrel waits for open requests up to `HostOptions.ShutdownTimeout`. The stream catches the cancellation and ends normally, because an exception after the headers leaves the response without its end.
- `KilerHost.ShutdownTimeout` (5 s) stays shorter than `SingleInstance.Wait` (10 s), so a second start gets the lock of a Kiler that is stopping. The guard sees only whole requests, so a client without the token can hold a half-sent one until this limit.
- `/api/health` puts the stop off, because a second start asks it before it opens the browser. Once the host is stopping, it and `/api/events` answer 503.
- Events are `SseItem<string>` with a name. One stream has one data type, so the events that carry data (M6) will put it there as a string; its format is decided in M6. A string is written as it is, without the HTTP JSON options.
- Tests: `Heartbeat` and the stream on `FakeTimeProvider`; the stop on the real Kestrel with short real waits (`ShutdownTests`). There the ping interval stays long, so a closed page is noticed by its closed connection, not by a failed ping.

## Local copy (M1.5a)

- Started from a network folder (a UNC path or a mapped network drive), `LocalCopy` copies the exe to `%LOCALAPPDATA%\Programs\Kiler` when there is no copy, writes the folder to `%LOCALAPPDATA%\Kiler\share.json`, makes `Kiler.lnk` in the Start menu when it is missing, starts the copy and exits. The exe in the profile, under any name, never copies itself.
- The single instance comes first. The lock is let go before the copy starts, and the copy takes it.
- An existing copy is never replaced: only the copy checks a new version (M3). The copy goes through a temporary file and a rename that does not overwrite.
- The copy starts without the shell, in its own folder, with `StartOptions.ToServerArguments()`: `--data` as a full path, because the instance names come from it. Its console is inherited and not redirected, so nothing reads its output, which can hold the `Open:` line.
- A failed copy or start ends with exit code 1. Kiler never runs from the shared folder instead. A failed shortcut is only a warning.
- `--help` and `--diag` run where they are and copy nothing.
- Every start from the shared folder runs the exe there first, and anyone can replace it. So the start says when the shared folder changes, and that the next starts go through the Start menu.
- `ICopyFiles`, `IShortcuts` and `IProcessStarter` live in Kiler.App, as `IBrowser` does: only the start of the exe uses them.

## UI files and publish (M1.8)

- A Release build and a publish run `npm ci` and `npm run build` in `src/ui` (Kiler.App.csproj) and embed `dist/` under `ui/`. They need Node.js 24.15 or newer on PATH. Each npm step runs only when its inputs are newer than its outputs: `npm ci` when `package-lock.json` is newer than `node_modules\.package-lock.json`, the UI build when a file in `src/ui` is newer than the stamp in `obj\` or `dist\index.html`. A Debug build embeds nothing and answers 404 for the UI: in dev mode Vite serves it (`npm run dev`), and `npm ci` would replace the `node_modules` that the dev server runs from. Do not run a Release build while `npm run dev` runs and `package-lock.json` has changed.
- The manifest targets come from the package `Microsoft.Extensions.FileProviders.Embedded`, referenced with `IncludeAssets="build"`; the types come with the framework. `GenerateEmbeddedFilesManifest` is on only when the UI is embedded: with no embedded files the package warns and makes no manifest, and `UiFiles.Embedded()` then gives a `NullFileProvider`.
- `UiFiles.Use` comes after the guard and after routing, so an endpoint wins over a file. Outside `/api`, an address whose last segment has no dot is a page of the UI and gets `index.html`; a name with a dot is a file, and a missing one is 404. `/api` never gets a file. So the routing of the UI (M6) uses addresses without a dot in the last segment.
- The headers of every UI file: `Content-Security-Policy` (`UiFiles.ContentSecurityPolicy`: `default-src 'self'`, `data:` images only, no plugins, no `<base>`, forms to Kiler only, no framing), `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: same-origin`. Not `no-referrer`: with it the browser sends `Origin: null` on Kiler's own POST requests. The policy is on the scripts too, because a worker takes the policy of its own script. A change of the policy (M6, M7) is a decision of its task and gets a security review.
- Caching: `/assets/*` (the names with a hash) `private, max-age=31536000, immutable`, `index.html` `no-store`, other files `no-cache`.
- `KilerHost.Build(options, uiFiles)` takes the UI files; the tests give made-up ones (`MadeUpUi`, a `PhysicalFileProvider` on a temporary folder). `EmbeddedUiTests` check the real build and are skipped in Debug, so `test.ps1` and CI, which build Release, run them.
- `RuntimeIdentifier` is `win-x64` in Kiler.App.csproj. Only the publish is self-contained, so Kiler.App.Tests can reference the exe.
- `publish.ps1` publishes `artifacts\publish\Kiler.exe` alone, and the native PDB to `artifacts\symbols` through `KilerSymbolsDir`. It stops when anything else is in the publish folder. The Web SDK's static web assets are off, the XML documentation and the managed PDBs of the referenced projects are not published.
- A DLL with the name of a system DLL, put next to the exe in the shared folder, must not load (M1.5a): `DefaultDllImportSearchPaths(System32)` for Kiler's own P/Invokes and `/DEPENDENTLOADFLAG:0x800` (`LinkerArg`) for the DLLs that the exe imports. `AssemblyTests` checks the attribute; `dumpbin /loadconfig Kiler.exe` shows `0800 Dependent Load Flag`. The runtime and the system DLLs still load `BCrypt.dll`, `ntdll.dll` and `NTASN1.dll` from the exe folder at run time (measured in M1.8): a residual risk for M3, with `SetProcessMitigationPolicy` and `NoRemoteImages` as the candidate fix.
- A change of the MSBuild targets is checked from a fresh `git clone` in a temporary folder (`build.ps1`, then `test.ps1 -NoBuild`), as CI builds. A target that runs before `PrepareForBuild` finds no `obj\<configuration>\<framework>\<runtime>\` there and creates what it writes into (the first CI run of M1.8 failed on it).
- `npm ci` runs with `--ignore-scripts`: no package runs code in the build. `publish.ps1` sets `KilerFreshUi`, so the published exe comes from a clean `npm ci` and a new UI build.

## Smoke tests (M1.9)

- `SmokeTests` in Kiler.App.Tests run the published `Kiler.exe` as a process. They carry the trait `Category=Smoke` and run only when `KILER_SMOKE_EXE` names the exe (`SkipUnless`). `smoke.ps1` sets it to `artifacts\publish\Kiler.exe` (or `-Exe <path>`) and runs only them, with `--fail-skips on`, so a skip fails the run there. `test.ps1` shows them as skipped. Run `publish.ps1` first: after a change of the code, the tests otherwise run the old exe.
- What goes there: what only the published exe shows, such as Native AOT, the embedded UI, the PE header, the console and the exit codes, the single instance across processes, and the environment that a builder would read. The rest goes into the API tests on the real Kestrel, which are faster and count for coverage.
- `KilerExe` copies the exe into a temporary folder on the local disk, so it does not copy itself into the profile (M1.5a), with a data folder of its own, so it is an instance of its own (M1.5). Every start gets `--no-browser --data <folder>` first: a test never opens a browser. `KilerExe.Diag` starts `--diag` with only the arguments of the test, because `--diag` refuses `--no-browser` (M1.11). Disposing it kills the runs that are left and deletes the instance file that a killed run leaves in the real `%LOCALAPPDATA%\Kiler`.
- xUnit printed part of a token when a check of the output failed (see "Tests"). So `KilerRun.Output` and `Errors` hide the token (`KilerExe.MaskToken`: every `t=` value and every word of 43 base64url characters), the raw `Open:` line comes only from `WaitForLineAsync`, and the token and the cookie are compared with `Assert.True`.
- `KilerExe.Dispose` always deletes the instance file and the temporary folder, also when a run cannot be ended, and tries the folder a few times, because an antivirus may still read the exe that has just run.
- Waits: short `--first-wait` and `--idle-wait`, the lower bound from a time stamp taken before the action, the upper bound as the timeout of `WaitForExitAsync`. A health check puts the stop off by the first wait (M1.6), so a test that asks it and then wants the idle wait first waits out the first wait from the health check. The first wait also gives the test its time to open a page after the start: 3 s or more, for a busy CI machine.
- A smoke test is checked to fail against a broken exe: break the code, `dotnet publish src\Kiler.App\Kiler.App.csproj -c Release -o <scratch folder>`, run the tests with `KILER_SMOKE_EXE` set to that exe, then put the code back.

## Diagnostics (M1.11)

- `--diag [<folder>]` (`DiagMode`) runs before the single instance and the local copy: no server, no browser and no copy, also from the shared folder. Without a folder it takes `--data`, else `data\` in the shared folder (`DiagTarget`): the folder of the exe when it runs from a network folder, the folder in `share.json` otherwise. The folder must exist; nothing creates it.
- `FileDiag` writes only into its own temporary subfolder. It deletes only the files in its list, then the subfolder without `recursive`, and nothing when the subfolder has become a link. Keep it so: the folder can be the real data folder.
- On the shared folder everyone can rename folders. A folder that Kiler writes into and deletes from by path is held by `PinnedFolder` from the moment it is made until its files are deleted: a handle with a data access (`FILE_LIST_DIRECTORY`) and without delete sharing, opened as itself (`FILE_FLAG_OPEN_REPARSE_POINT`), which refuses a link. Windows then refuses to rename it or a folder above it, also over SMB. A handle with only attribute access does not count in that check (M1.11).
- The report file goes next to the exe, which can be the shared folder that everyone reads. Every line goes through `DiagReport`, and every folder or account name that a line can show is registered with `HidePath` or `HideName`, so the file shows a placeholder. A new line with a path, an account or an exception message needs that. `ForFile` replaces in one pass, the longest value first, the later of two equal ones, and a name only as a whole word. Only the accounts of Windows itself (SYSTEM, LOCAL SERVICE, NETWORK SERVICE, BUILTIN) keep their name; every other account is hidden, Microsoft Entra, NAS identities and NT SERVICE accounts (which carry a service name) too.
- The numbers use `CultureInfo.InvariantCulture`. A test under another culture creates it with `useUserOverride: false`: the developer machine overrides the decimal comma of bg-BG.
- The owner checks fail on a local disk in a process whose token gives new files to the Administrators group (a run as administrator, possibly the CI machine). The tests expect what `FileDiagTests.OwnerIsTheUser` says.
- The checks in the real environment, `--diag` among them, are in `docs/real-environment-checks.md`.

## Data layer (M2)

Decided at the start of M2 (`docs/tasks/M2.md`, "Decisions for the module"):

- **Documents.** Every JSON file that Kiler writes in `data\` is a document with `id` and `version` at the top: records, users, templates, stashes, `settings.json`, `levels.json`, `key.json`. Kiler.Storage reads only these two and keeps the rest as it is; it sets `version` itself on every write. The typed models (records, users, templates, stashes, settings) live in Kiler.Core with their modules (M4, M5) and their own source-generated JSON, not in Kiler.Storage.
- **Layout.** `history\` and `trash\` repeat the folders: `history\records\<id>\vN.json`, `history\settings\vN.json`, `trash\records\<id>.json`. A private folder `private\<account>\` is a root of its own with the same code.
- **Writes.** A temporary file, the old version into its history slot, a rename; then the creation time is set equal to the write time (file tunneling). Once started, a write runs to its end: the stop aborts requests after 5 s (M1.6).
- **The lock** is the history slot, created with create-new (`FileMode.CreateNew`), which is atomic also over SMB: the second writer of the same version gets a conflict. Do not replace it with a check followed by a rename.
- **The local copy** follows only the changes that Kiler makes: new versions, history slots, moves to `trash\`. A file that disappears outside the trash, is damaged or was edited in place keeps its last good copy apart, and is reported for restore.
- **Names from the share** become paths, also in the local copy in the profile. Anyone can write the shared folder, so a name is checked before a path is built from it, and links are not followed.
- **The name check** (`StorageName`, M2.1): lowercase ASCII letters, digits, `.`, `-` and `_`, 1 to 64 characters; no leading or trailing dot, no `..`, no device name of Windows (also with an extension), no `.tmp` at the end, which is kept for temporary files and folders. Lowercase only, because a NAS may tell case apart and the local copy in the profile cannot. So M4 and M5 make every id and the folder name of an account in lowercase. The exception message never repeats a refused name.
- **Paths** come only from `DataFolder` (`DocumentPath`, `HistoryPath`, `TrashPath`) with a `DocumentKey` (`InFolder`, `AtRoot`), which checks both names and has no `init` accessors. Never join a raw name into a path.
- **Roots** (M2.1): `DataFolder.Create` needs the folder above and takes an existing folder only when it holds nothing but empty folders of the layout; `Open` creates nothing; a root that is a link is refused. `CreatePrivate` makes `private\<account>.<random>.tmp\` and renames it without replacing, and refuses a private folder that exists, even an empty one: whoever makes it first is its owner (design 7.8). A new root gets only the top folders; `history\<folder>\` and `trash\<folder>\` come with the first write or delete.
- **File operations** go through the internal `IFileSystem` (`FileSystem` on System.IO), so tests can put `TestFileSystem` with its hooks in its place. `CreateFolder` makes one folder in a folder that exists, never a chain, and refuses a link or a file that is already in its place. A link is any reparse point, from the attributes of the listing; the folder that is listed is checked with one request.
- **Document format** (M2.2): `DocumentFormat.New` and `Next` make the bytes and `Read` checks them; nothing else writes or parses a document file. A written file: UTF-8 without a BOM, indented by 2 spaces, LF, a line end after the object, `id` then `version` then the content in its order, non-ASCII as it is. The limits are `DocumentFormat.MaxSize` (1 MiB) and `MaxDepth` (32). A read is strict, and a damaged file gives a `DocumentDamage`, not an exception: invalid UTF-8, comments, trailing commas, a name twice at any level, an escaped lone surrogate, too deep, too large, a wrong `id` or `version`, and `ID` or `Version` beside the layer's own. A stream is read up to `MaxSize` + 1 bytes and no further. `JsonNode.Parse` alone checks none of the UTF-8 inside strings, the lone surrogates or the BOM; the details are in the notes of M2.2.
- **Documents in the upper layers** (M4, M5): `Document.Content` is the object without `id` and `version`; they come only from `Document.Key` and `Document.Version`, and a write drops them from the content. The models read the content with case-sensitive JSON contexts, not the web defaults of `KilerJsonContext`, so a name in another case means nothing to them.
- **A read and a rename** (measured in M2.2): a rename over a file that another handle holds open fails with access denied, even when the reader shares delete. `IFileSystem.OpenRead` shares read, write and delete, and the writes of M2.3 handle the rest.
- `share.json` is not checked again when it is read.

## Local folder

- `%LOCALAPPDATA%\Kiler` holds the instance file with the token, `share.json` and, from M2, the local copy of the data. `PrivateFolder.Create` makes it with a protected ACL: the user and SYSTEM, nothing inherited. A folder that exists is left as it is.
- Everything in it is written through `PrivateFolder.WriteFile` (a temporary file and a rename), or into a folder made by `PrivateFolder.Create`.
- The folders of the profile come from `UserFolders` (the known folders of Windows), not from environment variables.
- Kiler.App and Kiler.App.Tests carry `[assembly: SupportedOSPlatform("windows")]` from their project files, so the platform analyzer accepts Windows APIs there without guards. The other projects stay platform-neutral.

## Style

The build enforces `.editorconfig` (M1.1): file-scoped namespaces, usings outside the namespace and no unnecessary ones, braces, explicit accessibility, readonly fields. Names: interfaces `IName`, private fields `_camelCase`, private constants and private static readonly fields `PascalCase`.

Files keep the line endings they have in the working tree: LF for almost all, CRLF for the few that the .NET tools wrote (`.gitignore`, `Kiler.slnx`, `dotnet-tools.json`). A script that rewrites a file keeps them; Python on Windows writes CRLF in text mode (M1.6). Check them by the bytes: `grep` in Git Bash can show a wrong count.

The rest are general good practices, not project decisions. If a task asks for something else, the task wins.

- Nullable is on. Use `!` only with a reason.
- Async all the way, with `CancellationToken`. No `.Result` or `.Wait()`.
- Time comes from `TimeProvider`, not `DateTime.Now`, so tests can use `FakeTimeProvider`.
- Dispose `IDisposable` resources with `using`.
- Passwords, keys, tokens and decrypted values never go into logs, exception messages or `ToString()`. A record prints all its properties, computed ones too, so a record that holds one overrides `ToString()`.

## Windows parts

Per PLAN section 3, the current Windows account, DPAPI and opening Explorer are interfaces in Kiler.Core, implemented in Kiler.App. Their names are chosen when they are first needed. Tests use fake implementations.

The file owner is the exception (decided at the start of M2): an interface in Kiler.Storage, implemented in Kiler.App through `FileOwner`. Storage reads the owner through the handle it reads the file with and keeps it in the local copy, so a start from the local copy does not ask the share for every owner.

Opening the browser is `IBrowser` in Kiler.App (decided in M1.5), because only the start of the exe opens a browser. Tests use a fake, and no test starts the real browser.

## Tests

- **xUnit v3** (package `xunit.v3`) with the built-in `Assert`. No FluentAssertions: since v8 its license is paid for commercial use.
- **Microsoft Testing Platform.** xunit.v3 4.x runs on MTP v2, and `global.json` sets it as the `dotnet test` runner. Do not add the VSTest packages (`Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`, `coverlet.collector`); coverage uses `coverlet.MTP`.
- One test project per project in `src/`. `tests/Directory.Build.props` gives them `xunit.v3`, `OutputType` Exe and a global `using Xunit`.
- API tests run on the real Kestrel through `KilerServer` in `Kiler.App.Tests`. It starts `KilerHost` on a free port of 127.0.0.1 and gives clients with or without the cookie. WebApplicationFactory is not used (decided in M1.4): its test server has no port, so the Host check rejects it, and it runs `Main`, which starts real things from M1.5 on (mutex, `instance.json`, browser).
- **TDD in Kiler.Crypto and Kiler.Storage:** write the test first, run it and see it fail, then write the code.
- A test name states the scenario and the expected result.
- File tests work in a temporary folder that is deleted after the test. In Kiler.Storage.Tests it comes from `TestFolder` and nowhere else, so `KILER_TEST_FOLDER` can point all of them to a temporary subfolder of the real shared folder (M2.11); the test folders are then made right in it. `TestFolder.CreateJunction` makes a junction and skips the test off a local disk, where Windows cannot make one. `Directory.Delete` with `recursive` fails on a junction for a user who is not an administrator, because it calls `DeleteVolumeMountPoint`, so `TestFolder` deletes the links first (M2.1).
- Tests do not use the external network and do not depend on run order. The local server on loopback is allowed; API and smoke tests use it.
- Test data is made up. No real system names, hosts, accounts or passwords. The example names of the design can be terms of `data-guard`, so do not copy them; take names such as `demo.user` (found at the start of M2).
- An assertion prints what it compares, and the message of a failed test goes into the public CI log. A token, a password, a key or a decrypted value is compared with `Assert.True` and a message without it, or masked first; `Assert.Equal`, `Assert.Single` and `Assert.Contains` print the values or the whole collection (M1.9).
- xUnit v3 treats a run with zero tests as a failure.
- A new test is checked to fail against broken code: break or take out what it guards, see it fail, put the code back. A broken version that does not build proves nothing, because the tests then run against the old binaries (M1.3). Taking out the last use of a `using` breaks the build with IDE0005, so change an argument instead of taking out the call (M2.2).
- The Write and Edit tools turn a JSON or C# escape of a backslash, `u` and four hex digits in their text into the character, in code, in comments and in Markdown alike; only a lone surrogate stays as it is. A test that needs such an escape in its JSON builds it in code (`DocumentFormatTests.Escaped`), and the bytes of a file with one are checked afterwards (M2.2).
- Tests on the real clock, such as the stop by the heartbeat: take the time stamp before the action that starts the wait, check a lower and an upper bound, and leave margins for a slow CI machine (`windows-latest`) (M1.6).

## Manual checks

- Publish with `powershell -ExecutionPolicy Bypass -File .\publish.ps1` (M1.8), then run the smoke tests with `powershell -ExecutionPolicy Bypass -File .\smoke.ps1` (M1.9). Native AOT needs the Visual Studio C++ tools and `vswhere.exe`; the script puts the Visual Studio Installer folder on PATH when `vswhere.exe` is not there (M1.0b, M1.3). `artifacts\` is out of git.
- The UI of the published exe is checked in Chrome or Edge, not in the Claude Code in-app browser, which blocks the network requests of a SharedWorker and so shows "Stopped" (kiler-ui, Manual checks).
- Kiler.exe started by hand or from a script runs with `--data <temporary folder>`, so it does not meet the user's own Kiler, and from a local folder, so it does not copy itself into the profile (M1.5a). Afterwards, check that no `instance-*.json` was left in `%LOCALAPPDATA%\Kiler`.
- The `Open:` line holds the token. A script that reads it masks the token in everything it prints.
- Requests from the shell to the local server must not go through the corporate proxy. In the Claude process `NO_PROXY` covers 127.0.0.1 and localhost. With curl's `--noproxy`, quote the `*`, or bash expands it to file names (M1.4).

## Deferred

Nothing at the end of M1; the build and test commands are in CLAUDE.md, section "Code" (M1.12). Open questions go here with the module that decides them; the current ones are in PLAN section 6.

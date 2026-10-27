# Kiler.App: the host

Part of the `kiler-dotnet` skill. Read it with [SKILL.md](SKILL.md) when a task changes Kiler.App or its tests.

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
- xUnit printed part of a token when a check of the output failed (see "Tests" in SKILL.md). So `KilerRun.Output` and `Errors` hide the token (`KilerExe.MaskToken`: every `t=` value and every word of 43 base64url characters), the raw `Open:` line comes only from `WaitForLineAsync`, and the token and the cookie are compared with `Assert.True`.
- `KilerExe.Dispose` always deletes the instance file and the temporary folder, also when a run cannot be ended, and tries the folder a few times, because an antivirus may still read the exe that has just run.
- Waits: short `--first-wait` and `--idle-wait`, the lower bound from a time stamp taken before the action, the upper bound as the timeout of `WaitForExitAsync`. A health check puts the stop off by the first wait (M1.6), so a test that asks it and then wants the idle wait first waits out the first wait from the health check. The first wait also gives the test its time to open a page after the start: 3 s or more, for a busy CI machine.
- A smoke test is checked to fail against a broken exe: break the code, `dotnet publish src\Kiler.App\Kiler.App.csproj -c Release -o <scratch folder>`, run the tests with `KILER_SMOKE_EXE` set to that exe, then put the code back.

## Diagnostics (M1.11)

- `--diag [<folder>]` (`DiagMode`) runs before the single instance and the local copy: no server, no browser and no copy, also from the shared folder. Without a folder it takes `--data`, else `data\` in the shared folder (`DiagTarget`): the folder of the exe when it runs from a network folder, the folder in `share.json` otherwise. The folder must exist; nothing creates it.
- `FileDiag` writes only into its own temporary subfolder. It deletes only the files in its list, then the subfolder without `recursive`, and nothing when the subfolder has become a link. Keep it so: the folder can be the real data folder.
- On the shared folder everyone can rename folders. A folder that Kiler writes into and deletes from by path is held by `PinnedFolder` from the moment it is made until its files are deleted: a handle with a data access (`FILE_LIST_DIRECTORY`) and without delete sharing, opened as itself (`FILE_FLAG_OPEN_REPARSE_POINT`), which refuses a link. Windows then refuses to rename it or a folder above it, also over SMB. A handle with only attribute access does not count in that check (M1.11). `PinnedFolder` is in Kiler.Storage since M2.3; the hold does not stop an empty folder from being made a mount point in place, which `ThrowIfLink` sees through the handle.
- The report file goes next to the exe, which can be the shared folder that everyone reads. Every line goes through `DiagReport`, and every folder or account name that a line can show is registered with `HidePath` or `HideName`, so the file shows a placeholder. A new line with a path, an account or an exception message needs that. `ForFile` replaces in one pass, the longest value first, the later of two equal ones, and a name only as a whole word. Only the accounts of Windows itself (SYSTEM, LOCAL SERVICE, NETWORK SERVICE, BUILTIN) keep their name; every other account is hidden, Microsoft Entra, NAS identities and NT SERVICE accounts (which carry a service name) too.
- The numbers use `CultureInfo.InvariantCulture`. A test under another culture creates it with `useUserOverride: false`: the developer machine overrides the decimal comma of bg-BG.
- The owner checks fail on a local disk in a process whose token gives new files to the Administrators group (a run as administrator, possibly the CI machine). The tests expect what `FileDiagTests.OwnerIsTheUser` says.
- The checks in the real environment, `--diag` among them, are in `docs/real-environment-checks.md`.

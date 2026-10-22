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
- No `Activator.CreateInstance`, no `Type.GetType` from a string, no dynamic assembly loading, no `dynamic`.
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
- `AccessToken` is made in `KilerHost.Build` and reaches other code through DI. It never goes into the command line, the environment, logs, exception messages or the console. The one exception is the address printed with `--no-browser`. It is compared only with `AccessToken.Matches`, which is constant-time.

## Style

The build enforces `.editorconfig` (M1.1): file-scoped namespaces, usings outside the namespace and no unnecessary ones, braces, explicit accessibility, readonly fields. Names: interfaces `IName`, private fields `_camelCase`, private constants and private static readonly fields `PascalCase`.

The rest are general good practices, not project decisions. If a task asks for something else, the task wins.

- Nullable is on. Use `!` only with a reason.
- Async all the way, with `CancellationToken`. No `.Result` or `.Wait()`.
- Time comes from `TimeProvider`, not `DateTime.Now`, so tests can use `FakeTimeProvider`.
- Dispose `IDisposable` resources with `using`.
- Passwords, keys, tokens and decrypted values never go into logs, exception messages or `ToString()`.

## Windows parts

Per PLAN section 3, the current Windows account, DPAPI, the file owner and opening Explorer are interfaces in Kiler.Core, implemented in Kiler.App. Their names are chosen when they are first needed. Tests use fake implementations.

Opening the browser is also behind an interface, so tests can replace it (M1.5). Where that interface lives is decided in M1.5.

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

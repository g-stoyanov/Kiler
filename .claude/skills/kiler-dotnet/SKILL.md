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

## AOT-ready code

The exe is a self-contained single file. Native AOT is decided after the checks in the real environment (design section 14). The code must be able to move to AOT without rework:

- JSON only with source generation (`JsonSerializerContext`). No reflection-based serialization.
- No `Activator.CreateInstance`, no `Type.GetType` from a string, no dynamic assembly loading, no `dynamic`.
- Minimal APIs. No MVC, no Razor.
- The trim and AOT analyzers are on and warnings are errors. Do not suppress them without a comment that explains why.

## Style

These are general good practices, not project decisions. If a task asks for something else, the task wins.

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
- One test project per project in `src/`. API tests use WebApplicationFactory in `Kiler.App.Tests`.
- **TDD in Kiler.Crypto and Kiler.Storage:** write the test first, run it and see it fail, then write the code.
- A test name states the scenario and the expected result.
- File tests work in a temporary folder that is deleted after the test.
- Tests do not use the external network and do not depend on run order. The local server on loopback is allowed; API and smoke tests use it.
- Test data is made up. No real system names, hosts, accounts or passwords.
- xUnit v3 treats a run with zero tests as a failure.

## Deferred

| Question | When |
|---|---|
| Native AOT | after the checks from design section 14 |
| Build and test commands | M1.12, in CLAUDE.md |

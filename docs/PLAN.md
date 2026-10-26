# Kiler: development plan

> Phase 2 of the course project, 21.10.2026.
>
> The behavior of the application is described in the design document `KILER_DESIGN.md`. It is not in the public repo, because it contains internal names. The references "design section X" point to its sections.

---

## 1. How the plan is used

- The modules are done in the order from section 4.
- Before each module, its open questions (section 6) are resolved and the module is broken into tasks in [docs/tasks/](tasks/README.md). Only then is code written. Progress is also tracked by the tasks.
- If the code has to deviate from the design, ask first.
- A module is done when:
  - the "Done" criteria from its description are met;
  - the tests pass in CI;
  - there is no real data in the code, tests and demo data;
  - the course note is recorded with `/note`.
- The plan covers the whole application. Only in the exam write-up are modules merged (section 7).

---

## 2. Technologies

| Area | Choice |
|---|---|
| Platform | .NET 10, C#. The exe is published with Native AOT, `win-x64` only. Each user runs a local copy in the profile (M1). |
| Server | ASP.NET Core minimal API (Kestrel) on `127.0.0.1` |
| UI | React 19, TypeScript, Vite. The finished build is embedded in the exe. |
| Code in the UI | CodeMirror 6, sql-formatter (with PL/SQL) |
| Documents | markdown-it, DOMPurify, Mermaid |
| Data | JSON files, System.Text.Json |
| Cryptography | built into .NET: AES-GCM, PBKDF2-SHA256, HMAC-SHA256, ECDSA P-256 (signed updates), DPAPI |
| C# tests | xUnit v3 (package `xunit.v3`), the built-in assertions, on Microsoft Testing Platform (the `dotnet test` runner is set in `global.json`) |
| API tests | the real Kestrel on `127.0.0.1` with `HttpClient`, not WebApplicationFactory (decided in M1.4) |
| UI tests | Vitest |
| E2E | Playwright for .NET (`Microsoft.Playwright.Xunit.v3`) |
| Coverage | coverlet (`coverlet.MTP`) and ReportGenerator, no required threshold |
| Code style | `dotnet format`, ESLint, Prettier |
| CI | GitHub Actions, `windows-latest` |

The exact versions are fixed when the projects are created: the latest stable ones at that time. Node.js is needed only for the UI build. Users get only the exe.

---

## 3. Solution structure

```
Kiler.slnx
global.json                SDK version, test runner
Directory.Build.props      shared settings: nullable, warnings, AOT analyzers, version
Directory.Packages.props   NuGet package versions
.editorconfig              code style
dotnet-tools.json          local dotnet tools: ReportGenerator
build.ps1                  build; CI and developers run the same scripts
test.ps1                   tests with coverage and the coverage report
format.ps1                 format check
check-ui.ps1               UI checks in src/ui: Prettier, ESLint, type check, Vitest
publish.ps1                publish: Kiler.exe alone in artifacts\publish, its PDB in artifacts\symbols
smoke.ps1                  smoke tests against the published Kiler.exe
src/
  Kiler.Crypto/            cryptography, no dependencies
  Kiler.Storage/           files: writing, history, trash, cache, change watching
  Kiler.Core/              templates, records, search, users, permissions, audit
  Kiler.App/               Kiler.exe: host, API, the Windows implementations
  ui/                      React + Vite; the build is embedded in Kiler.App
tools/
  Kiler.DemoData/          generator of made-up demo data
tests/
  Directory.Build.props    test settings: xUnit v3, no AOT analyzers
  Kiler.Crypto.Tests/
  Kiler.Storage.Tests/
  Kiler.Core.Tests/
  Kiler.App.Tests/         API tests and smoke tests of the exe
  Kiler.E2E/               Playwright
docs/
  PLAN.md
.github/workflows/
  ci.yml
```

Dependencies:

```
Kiler.App ──► Kiler.Core ──► Kiler.Crypto
                   │
                   └───────► Kiler.Storage
```

- Kiler.Crypto and Kiler.Storage do not know about each other. Kiler.Core connects them.
- The Windows parts are interfaces in Kiler.Core: current account, DPAPI, file owner, Explorer. Their implementations are in Kiler.App. This way the core tests use fake implementations.
- Opening the browser is an interface in Kiler.App (decided in M1.5), because only the start of the exe opens a browser. The same holds for copying the exe, the Start menu shortcut and starting the copy (M1.5a).

---

## 4. Work order

```
M1 Host and skeleton
 └► M2 Data layer
     └► M3 Cryptography
         └► M4 Users and permissions
             └► M5 Templates, records and search
                 └► M6 User interface
                     └► M7 Documents and diagrams

M8 Tests and automation: starts with M1 and runs through all modules
```

The screens are built together with the modules:

- M1 has the UI skeleton;
- M4 has the sign-in and user screens;
- M6 has the main view and the records;
- M7 has the documents.

---

## 5. Modules

### M1. Host and skeleton

Design sections 4 and 14.

**Goal:** `Kiler.exe` is started with a double click, starts a local server and opens the browser. Started from the shared folder, it copies itself to the user's profile and runs from there. The same exe is used for the checks from design section 14.

**Before the steps:**

- Git hooks `pre-commit`, `commit-msg` and `pre-push` that call the `data-guard` script, so commits made outside Claude are checked too (M1.0a).
- A probe exe, `tools/Kiler.Probe`, published the same way as `Kiler.exe`, for the checks from design section 14 before the real code is written (M1.0b). It starts a local server and opens the browser, and with `--diag <folder>` it measures many small files and checks the file owner. Only a summary of its results goes into the repo: the notes under M1.0b in [tasks/M1.md](tasks/M1.md). The decisions made after it are in this plan. M1.11 moved its `--diag` logic into `Kiler.exe` and deleted it.

**Steps:**

1. Solution and projects as in section 3, `Directory.Build.props`, `.editorconfig`.
2. ASP.NET Core on `127.0.0.1`, on a free port.
3. Protection of the local server:
   - the address the browser is opened with contains a random 32-byte token;
   - on the first request, the token is exchanged for an HttpOnly cookie (`SameSite=Strict`) and the address is cleared. Cookies are not separated by port, so its name is per instance (decided in M1.7): `kiler`, and `kiler-<key>` for a start with `--data`, the key of step 5. Two Kilers that run at the same time, such as the dev mode and the Kiler of the user, then do not overwrite each other's cookie. Not per port, because the port changes with every start and the browser keeps the old cookies;
   - a request without a valid cookie gets 401;
   - a request whose `Host` is not `127.0.0.1:<port>` or `localhost:<port>` gets 400, as protection against DNS rebinding;
   - a request that the browser marks as coming from another site (`Sec-Fetch-Site` other than `same-origin` or `none`) gets 403. Other ports of 127.0.0.1 are the same site for the browser, so `SameSite=Strict` does not stop their requests (decided in M1.4);
   - the UI files carry a Content Security Policy that allows only Kiler itself (`default-src 'self'`, `data:` images) and no framing. A module that needs more, such as CodeMirror or Mermaid, changes it in its own task (decided in M1.8).
4. Opening the default browser.
5. Single instance: a named mutex and a file with the port and the token in `%LOCALAPPDATA%\Kiler`. A second start only opens the browser to the running instance. Decided in M1.5:
   - the mutex is per user and session, and so is the file;
   - a start with `--data` is an instance of its own, so tests with their own data folder do not meet the user's Kiler;
   - the file is trusted only while another process holds the mutex; a file left over from a stopped process is deleted without contacting its port.
6. Shutdown: the page sends a signal that it is alive (heartbeat). If there is no signal for a few seconds, the process stops. The wait covers a page reload. Decided in M1.6:
   - each open page holds the server-sent event stream `/api/events`, which sends `ping` at once and then every 15 s; the server counts the open streams;
   - Kiler stops 10 s after the last stream closes, or 60 s after the start when no page opens; a new stream cancels the wait;
   - `/api/health`, which a second start asks before it opens the browser, puts the stop off by 60 s; once the stop has begun, it answers 503, and so does `/api/events`;
   - the streams end when the host starts to stop, so they do not hold the stop up.
7. Parameters for tests and diagnostics: `--no-browser`, `--port`, `--data <folder>`, `--diag`, and from M1.6 `--first-wait` and `--idle-wait`, which shorten the waits of step 6 for the smoke tests.
8. UI skeleton in `src/ui`:
   - Vite, React and TypeScript;
   - a common page frame, light and dark theme, an API client;
   - during development, the Vite dev server forwards `/api` to .NET;
   - on a Release build and a publish, MSBuild runs `npm ci` and `npm run build`, and the files are embedded as resources. A Debug build embeds nothing, because the dev mode serves the UI (decided in M1.8).

   Decided in M1.7:
   - `npm run dev` starts Kiler with `dotnet run -- --no-browser --data <repo>/dev-data`, reads the address with the token from its console, starts the Vite dev server with a proxy to it, and opens the browser on the dev server with the token. When Kiler stops, the dev server stops too;
   - all the pages of one Kiler share one event stream, held by a SharedWorker: without TLS the browser allows 6 connections per host, and a stream per page would stall the requests from the sixth page on. A browser without SharedWorker gets a stream per page;
   - all the UI texts live in one file, `src/texts.ts`.
9. Publishing: `dotnet publish` with Native AOT produces one `Kiler.exe`. Decided in M1.8:
   - `publish.ps1` puts the exe alone in `artifacts\publish` and its PDB apart in `artifacts\symbols`;
   - a DLL with the name of a system DLL, put next to the exe in the shared folder, is not loaded: P/Invoke and the imports of the exe look only in System32.
10. Diagnostic mode `--diag` for section 14: speed of listing and reading many small files, owner of a new file. It takes over the logic of the probe, and the probe is then deleted. Decided in M1.11:
    - `--diag` runs where it is started, before the single instance and the local copy, and starts no server;
    - without a folder it takes the data folder: `--data`, else `data\` in the shared folder, which is the folder of the exe when it runs from a network folder and the folder in `share.json` otherwise. The folder must exist; `--diag` does not create it;
    - the report file next to the exe shows placeholders instead of folders and account names, because it may stay on the shared folder;
    - the instructions for the checks in the real environment are in [real-environment-checks.md](real-environment-checks.md).
11. Local copy (decided after M1.0b): started from a network folder, the exe copies itself to `%LOCALAPPDATA%\Programs\Kiler` if there is no copy yet, remembers the shared folder (the data and the updates), and starts the local copy. It never overwrites an existing copy; updates are signed and come in M3. Decided in M1.5a:
    - the single instance is checked first;
    - the shared folder goes into `%LOCALAPPDATA%\Kiler\share.json` on every start from a shared folder, as it was started (a mapped drive stays a drive);
    - the Start menu shortcut is made through `IShellLinkW` with the COM source generator, which works under Native AOT;
    - `%LOCALAPPDATA%\Kiler` is made with a protected ACL: the user and SYSTEM;
    - every start from the shared folder runs the exe there first, so the start tells the user to use the Start menu from then on.

**Tests:**

- API tests of the protection: without a token, with a wrong token, with a foreign `Host`.
- Smoke test: the published exe with `--no-browser --port` answers a request and stops when there is no heartbeat.
- A second start does not start a second server.
- The decision about the local copy: started locally, no copy yet, a copy exists.

**Done:**

- a double click opens the browser with the page;
- a start from the shared folder runs the local copy;
- a second start uses the running instance;
- closing the page stops the process;
- CI passes;
- the exe is ready for the checks from design section 14.

---

### M2. Data layer

Design sections 5 and 7.10.

**Goal:** reliable reading and writing of the JSON files in the shared folder, when many people use it at the same time. The layer does not know about encryption. To it, the encrypted parts are plain strings.

**Steps:**

1. The structure of `data\` (section 5.1): `settings.json`, `levels.json`, `templates`, `stashes`, `users`, `records`, `history`, `trash`, `audit`, `private`. By default it is `data\` in the shared folder that the local copy remembers (M1), and `--data` changes it.
   - `private\<account>\` holds one user's private records (section 7.10): `key.json`, `records`, `history`, `trash`. The layer works with any root folder, so the private folder uses the same code.
2. Models and serialization with System.Text.Json and source generation.
3. Atomic write (section 5.4): a temporary file, a copy of the old version in `history\<id>\vN.json`, rename. The file is always created anew, and its creation time is set equal to the write time. It is set explicitly, because NTFS can give the new file the creation time of the old one on such a rename (file tunneling).
4. Versions and conflicts (section 5.5): write only if the version on disk is the expected one. Otherwise the result is "conflict" with the version from disk.
5. Deletion to `trash\` and restore from there. A list of the versions and restoring an old version (section 5.6).
6. Loading and local cache (section 5.3): startup is from the copy in `%LOCALAPPDATA%\Kiler`, after which only the changed files are synchronized. Files are read from the share in parallel, about 16 at a time: one by one, 2000 files take about 25 s, and 16 at a time about 1 s (M1.0b).
7. Watching for changes about every 10 seconds: one listing of the folder with names and dates, reloading the changed files, events to the upper layers. Folders it does not know, such as the `kiler-diag-*` subfolder of `--diag` (M1.11), which a stopped run can leave in `data\`, are ignored.
   - Only the current user's private folder is loaded and watched.
   - An admin's exe also copies the other users' private folders into its cache, without reading them, so they can be restored (step 10).
8. Resilience:
   - a corrupted JSON file is skipped and reported, without a crash;
   - when the network is unavailable, the application works from the cache and shows this.
9. File owner (section 7.8) as an interface, with a Windows implementation in Kiler.App, which reads the owner through `FileOwner.Of` (M1.11). A file whose write time is later than its creation time was edited in place, outside Kiler (decided after M1.0b); the layer reports it, and M4 marks it.
10. Restore from the local copy (decided after M1.0b): the share keeps no automatic copies, so missing or damaged files in `data\` can be written back from the cache in `%LOCALAPPDATA%\Kiler`. Here it is the API; the admin screen is in M6.

**Tests** (the tests are written first):

- atomic write with a simulated crash in the middle: the original stays intact;
- after a write, also over an old file, the creation time equals the write time;
- a file edited in place is reported;
- version conflict;
- history and trash;
- detecting a new, changed and deleted file;
- cache synchronization;
- restore from the cache;
- corrupted file;
- a private folder: the same operations with another root; other users' private folders are not loaded, and an admin's cache keeps a copy of them;
- loading 2000 files, with timing.

**Done:** a C# API for reading, writing, deleting, history and watching, covered by tests. The layer has no UI.

---

### M3. Cryptography

Design sections 4.1, 7.1–7.3, 7.5, 7.7 and 7.10.

**Goal:** a clean library with all cryptographic operations, with no files and no UI.

**Steps:**

1. Primitives: AES-GCM (12-byte nonce, 16-byte tag), PBKDF2-SHA256 with 600 000 iterations and a random salt, random 32-byte keys.
2. Format of the encrypted value: version number (for a future change of the algorithm), nonce, ciphertext and tag, encoded in base64.
3. Level keys: generation, `check` control values and their verification.
4. The user's keys: locking with their password, unlocking and locking again when the password changes.
5. Encrypting and decrypting a group of fields by level (the `protected` part of the record).
6. Admin seal (sections 7.3 and 7.8): HMAC-SHA256 over the user's file, with a key that only admins have.
7. Recovery key (section 7.7): generation, readable format, restoring admin access.
8. "Remember me": an interface for protecting a key with the Windows account. The DPAPI implementation is in Kiler.App.
9. Changing a level key (section 7.9): the group is encrypted again with the new key. The whole process is organized in M4 and M5.
10. Keys are zeroed in memory after use, as far as .NET allows.
11. Signed updates (decided after M1.0b):
    - an ECDSA P-256 signature over `Kiler.exe`, checked with the public key built into the exe; the check is in Kiler.Crypto;
    - the key pair is made once by the maintainer; the private key stays outside the repo and the shared folder;
    - `version.json` in the shared folder carries the version, the SHA-256 and the signature;
    - at start, the local copy (M1) reads `version.json`. For a newer version it writes the new exe next to itself as `.new` and checks the signature, renames its own running exe to `.old`, moves the new one into place and restarts. On any failure, the old exe goes back. The leftovers are deleted at the next start, with a few attempts, and a failure there never stops the application. They include a `Kiler.exe.*.tmp` that an interrupted first copy left (M1.5a). This part is in Kiler.App, and the probe in M1.0b checked that the antivirus allows it;
    - a tool that signs a release.
12. Personal key (section 7.10):
    - generation; locking with the password and with a personal recovery code; unlocking with either; locking again with a new password;
    - the personal recovery code has the same format as the recovery key (step 7);
    - a private record is encrypted whole with the personal key, with the owner and the record id as associated data.

**Tests** (the tests are written first):

- encrypting and decrypting gives the same text;
- a change of one byte is detected;
- a wrong password does not unlock the keys;
- verification with `check`;
- changing the password keeps the same level key;
- recovery with the recovery key;
- known test vectors for PBKDF2 and AES-GCM;
- an old version of the format is read;
- an update with a valid signature is accepted; a changed exe or another key is rejected;
- the personal key is unlocked with the password and with the recovery code; a private record does not decrypt under another id or another owner.

**Done:** Kiler.Crypto with full tests and no dependencies on the other projects.

---

### M4. Users and permissions

Design sections 7, 8.2 (audit) and 12.

**Goal:** sign-in, roles, locking, admin actions and checking permissions on the server. Plus the screens for them.

**Steps:**

1. Models for `levels.json` and `users\*.json`. The current Windows account is behind an interface.
2. "New Kiler" wizard (section 7.7):
   - setting the levels and the first admin;
   - generating the keys;
   - a recovery key, shown only once;
   - a warning while there is no second admin.
3. Sign-in (section 7.5): finding the file by the Windows account, password, checking the keys with `check`. A temporary password must be changed. The same password also unlocks the personal key, if there is one.
4. Guest mode: level 0, read-only, a "Sign in" button.
5. Session in the exe's memory:
   - keeps the unlocked keys;
   - automatic locking, overall and per level (for example Prod);
   - locking removes the keys and the decrypted data from memory.
6. "Remember me":
   - only for levels without `"remember": false`;
   - Prod and the personal key always ask for a password;
   - a single level is unlocked from the padlock.
7. Admin actions (section 7.6): new user, level change, new temporary password, "editor" and "admin" rights. Every user file gets a seal.
8. Detection (section 7.8):
   - users without a valid seal are marked as suspicious;
   - changes by people without `canEdit` are detected by the file owner;
   - files edited in place, outside Kiler (M2), are marked, because their owner is still the old author;
   - a file in a private folder whose owner is not the folder's owner is marked;
   - a record whose `updatedBy` is not the owner of its file is marked.
9. Permission check on the server on every request: read level, `canEdit`, `isAdmin`. Nobody writes a field above their level (section 7.4).
10. Audit (sections 8.2 and 12): showing and copying Prod passwords is recorded in `audit\<date>\<user>.jsonl`.
11. Removing an untrusted person (section 7.9):
    - a new key for the level;
    - all groups with this level are encrypted again;
    - the other users with this level get new temporary passwords.
12. Personal key (section 7.10):
    - it is created by the user's own exe with their first private record, never by the admin's exe, together with a personal recovery code that is shown only once and a warning that without the password and the code the private records are lost;
    - when a temporary password is changed, the previous password or the recovery code carries the personal key over to the new password; if this is skipped, the private records stay locked and can be unlocked later;
    - it is never remembered and locks after inactivity together with the highest level;
    - if it does not unlock, there is an error and never a new key in its place.
13. Screens:
    - the wizard;
    - sign-in and password change, with the field for the previous password or the recovery code;
    - locked screen and unlocking a level or the personal key;
    - the personal recovery code, shown once;
    - admin: users (including the suspicious ones) and levels.

**Tests:**

- sign-in with a correct and with a wrong password;
- a self-added user does not get in and is marked;
- a user who raised their own level does not have the key for it;
- an editor does not write above their level;
- automatic locking;
- Prod is not remembered;
- the admin creates a user, who signs in with the temporary password and must change it;
- audit entries;
- changing a level key;
- the personal key: the admin's exe cannot unlock it; it survives a level change (with the previous password) and a forgotten password (with the recovery code); it is not remembered;
- E2E: wizard → sign-in → the admin creates a user.

**Done:** all roles from section 7.4 work through the API and the screens, and permissions are checked on the server.

---

### M5. Templates, records and search

Design sections 5.2, 6, 7.10 and 11.

**Goal:** the record logic and the API for the UI.

**Steps:**

1. Template model and validation: field types (section 6.4) and their settings (section 6.5), `levelByEnv`, `ref`, selectable language.
2. The initial templates (section 6.1): "Server / database", "SQL query", "Link", "Note".
3. Record:
   - the mandatory header (section 6.1): a path of 1 to 4 stashes, name, description, tags, created and last updated on and by, private; the template's fields come after it;
   - the stashes, the name and the tags are level 0; the description is level 0 unless the author raises it;
   - the dates and the authors are set only by the exe, in UTC, and cannot be edited;
   - the fields are grouped by level, and the groups are encrypted with M3.
4. Field level: from the template, raised by the author or by the environment (`levelByEnv`). On a partial edit, the editor touches only the groups up to their level, and the higher ones stay untouched (section 5.2).
5. Locked fields are returned as "🔒 requires level X" instead of a value.
6. Passwords (`secret`):
   - never go into the list or the search;
   - "show" and "copy" go through a separate request;
   - for Prod, an audit entry is recorded (section 8.2).
7. Search and filters (section 11):
   - all records are in a list in memory;
   - the search text does not contain the passwords;
   - all words are searched, case-insensitive, in Cyrillic and in Latin;
   - the filters come from the fields with `filter`;
   - the stash tree with counters (step 12), filters by type and by tags (a record must have all the selected tags);
   - everything is updated on changes from M2.
8. `ref`: the title of the record it points to and a link to it. Handling the case when that record is deleted.
9. API:
   - search and reading a record;
   - creating and editing with a version (on conflict: 409 with both versions);
   - deleting, restoring from the trash, history;
   - templates (for admin);
   - stashes: the list and creating one; renaming, merging and deleting for the admin;
   - suggestions for tags and general settings.
10. Changing a template that has records (a removed field, a changed level): rules and re-encryption where needed.
11. Private records (section 7.10):
    - any signed-in user creates them, without `canEdit`; the guest has none;
    - the whole record is encrypted with the personal key (M3); only `id`, `template`, `version`, `owner` and the dates are in clear text;
    - they are in the search, the filters and the tree only for their owner, with a "Private only" filter;
    - a private record can `ref` a shared one, but a shared record cannot `ref` a private one;
    - "Make shared" turns a private record into a shared one, with the template's levels up to the user's level; it needs `canEdit`, and the private record goes to the private trash;
    - no audit.
12. Stashes (section 6.6):
    - one shared list, one file per stash in `stashes\`, with no levels and no parent; the name is unique, case-insensitive;
    - a record points to its stashes by id: 1 to 4, without gaps and without repeats;
    - any user with `canEdit` creates a stash; only the admin renames, merges (the merged stash keeps a redirect) and deletes (only when no shared record uses it; the stash stays marked as deleted);
    - private records use the shared stashes; a private record whose path goes through a deleted stash is shown at the position before it;
    - the tree is built from the paths of the records the current user sees: a node with no visible record is not shown, and the counters count only the visible records;
    - the stash names are part of the search text.

**Tests:**

- search in Cyrillic and in Latin;
- passwords are never found;
- `levelByEnv`;
- a partial edit keeps the group above the editor's level;
- an editor cannot change the environment so that the password is raised above their level;
- conflict;
- `ref`;
- a private record is never seen by another user, the admin or the guest, also through the API; "Make shared"; a shared record cannot `ref` a private one;
- stashes: a path with a gap or a repeat is rejected; a rename is seen everywhere; a merge keeps the paths; a stash used by a shared record cannot be deleted; a node with only invisible records is not shown, and its records are not counted;
- the tag filter;
- the dates and the authors of the header cannot be set through the API;
- 2000 records, with timing;
- API tests of all endpoints, including the permissions.

**Done:** the full API for records and templates, covered by tests.

---

### M6. User interface

Design sections 6.6, 7.10, 8 and 10.

**Goal:** the main view and all the work with records.

**Steps:**

1. Main view:
   - the stash tree with counters, collapsed to level 1 at the start; the open nodes are remembered locally; on a search or a filter, only the branches with results, expanded;
   - a "+" button on every node, which opens the new record form with the path filled in up to that node (section 6.6), and one above the tree for a record without a path; the guest does not see it;
   - filters by the fields with `filter`, by type and by tags;
   - global search with Ctrl+K and a delay of about 150 ms;
   - a list with the results (the fields with `list`);
   - private records with a mark and a "Private only" filter; while they are locked, one "🔒 Private records (N)" item with an unlock button.
2. Record view:
   - the header, with the stash path, where each part is a link to its node;
   - "copy" buttons;
   - passwords: hidden, with "show" and "copy", which get the value from the server;
   - padlocks with an unlock button.
3. Forms generated from the header and the template: creating and editing, required fields, raising the level, choosing a `ref`, tags with suggestions. A "Private" checkbox when creating, checked and locked for a user without `canEdit`, and "Make shared" on a private record.
   - Stashes (section 6.6): four drop-downs that filter while typing. The stashes already used at that position under the chosen path come first. The last option is "Create …" when nothing matches exactly. A position opens only after the one before it is chosen, and clearing it clears the ones after it.
   - From a "+" button: the positions filled in are locked until "Change" is clicked; the next position gets the focus, and if it stays empty, the record stays at that node.
   - When a stash name in the path matches a value of `env`, the form suggests that value; `env` stays a separate field.
4. Code (section 8.3):
   - CodeMirror 6 with the languages from the design, line numbers, search, themes;
   - "Copy" and "Format" buttons (SQL, including PL/SQL);
   - a "Parameters" button: fields for the bind variables and "Copy filled in".
5. Conflict (section 5.5): the two versions side by side and a choice.
6. History and trash (section 5.6): who changed what and when; restore with one click.
7. Admin screens (section 8.4):
   - templates;
   - stashes: the number of shared records for each, renaming, merging, deleting;
   - general settings: guest mode, path variables, locking;
   - history and audit;
   - restoring missing or damaged files from the local copy (M2).
8. Personal configuration (section 10): theme (light, dark or system), the values of the path variables, "remember me". It is kept in `%APPDATA%\Kiler\local-settings.json`.
9. Updating on changes from other users.

`markdown` fields are shown as text until M7.

**Tests:**

- Vitest: detecting the bind parameters, filling in a query, generating a form from a template.
- E2E: search and filters; creating and editing; showing and copying a password with audit; conflict; theme; a private record that another user does not see, locked and unlocked; the stash tree; creating a record with "+" on a node, at that node and one level deeper with a new stash; creating a stash from the form.
- Screenshots for the write-up.

**Done:** everything from design sections 8 and 10 works with the demo data.

---

### M7. Documents and diagrams

Design sections 9 and 12.

**Goal:** showing MD, Mermaid, PDF and images from the shared folder, from local paths and from http.

**Steps:**

1. The server reads the document (section 9.1):
   - from a UNC path;
   - from a local path with a variable (`{SVN}`);
   - from http(s) without sign-in or with Windows sign-in.

   It is read with the user's permissions. On denial, "no access" is shown.
2. Path variables (section 9.2): the list is in the general settings, the values are in the personal configuration. If a value is missing, the application asks on the first opening.
3. Security (section 12): the exe reads only paths from the records and the resources under the document's folder. It rejects `..` and any other attempt to get out of them.
4. Markdown (section 9.3):
   - markdown-it, sanitizing with DOMPurify, highlighted code;
   - images with a relative path;
   - links to other MD files open in the application;
   - a table of contents on the side and search in the document.

   The same view is used for `markdown` fields.
5. Mermaid (section 9.4):
   - strict mode;
   - zoom and pan, "show the code";
   - export as SVG and PNG, theme;
   - live preview when editing a `code` field with the `mermaid` language.
6. PDF and images (section 9.5). "Open in Explorer" works only for paths from the records.

**Tests:**

- substitution of the variables;
- denial on an attempt to get out of the allowed paths;
- a script in MD is removed;
- missing access;
- E2E: an MD document with a Mermaid diagram and an image.

**Done:** design section 9 works with made-up demo documents from the repo.

---

### M8. Tests and automation

Throughout the project.

**Steps:**

1. The test projects and shared helpers: a temporary data folder, fake Windows services (account, DPAPI, file owner). They are made together with M1.
2. `ci.yml` (on push and PR, on `windows-latest`). It starts with M1 and grows with the modules. The workflow calls scripts from the repo (for example `build.ps1` and `test.ps1`) instead of keeping the steps only in the GitHub YAML, so CI can move to another system later (section 9) and the same commands run locally. It includes:
   - cache for NuGet, npm and the Playwright browsers. Node with the npm cache came in M1.8, because the Release build runs npm;
   - `npm ci`, Prettier, ESLint, type check, Vitest (`check-ui.ps1`, M1.9), UI build;
   - `dotnet format --verify-no-changes`;
   - build, unit and API tests with coverage;
   - publish and smoke test of the exe. Decided in M1.9: the smoke tests live in Kiler.App.Tests with the trait `Category=Smoke` and run only when `KILER_SMOKE_EXE` names the exe; `smoke.ps1` sets it after `publish.ps1`, and `test.ps1` skips them;
   - E2E;
   - artifacts: the exe, the coverage report, the traces on failure, the screenshots. Decided in M1.9: the exe is uploaded only from runs that are not pull requests, and its SHA-256 goes on the run page. The exe for the exam and for the shared folder is taken from a push run on main, of a known commit, and checked against that SHA-256.
3. ~~`release.yml`: on a `v*` tag, a publish is made, plus a GitHub Release with `Kiler.exe` and its SHA256 checksum.~~ Dropped: a GitHub Release is useful only while the public repo exists. The CI artifact is enough for the exam, and in the company the exe is copied to the shared folder.
4. `tools/Kiler.DemoData`. It uses the Kiler libraries, so it encrypts correctly. It grows with M4–M7. It has two modes:
   - a made-up set of about 30 records: stash paths of several systems, all templates, tags, levels 0–3, an admin and two users with demo passwords, and a few private records of one of the users;
   - a "volume" mode with 2000 records.
5. Screenshots for the exam write-up from the E2E tests, with the demo data.
6. README for the public repo: what Kiler is, screenshots, how to build it and how to run the demo. How users start it: once from the shared folder, then from the Start menu (M1.5a).

---

## 6. Open questions

They are resolved at the start of the relevant module. The decisions are also recorded in the module's task file.

| When | Question |
|---|---|
| after M1 | The checks from design section 14 with the exe from M1. Decided: a probe exe before the code (M1.0b), and later a `--diag` mode (M1.11). Done in M1.0b: the architecture holds; the decisions are in M1, M2, M3, M4 and M6. The checkpoint with the final exe of M1 was done on 2026-10-26 and changes no decision ([tasks/M1.md](tasks/M1.md#checkpoint-in-the-real-environment)). |
| M1 | Native AOT or plain single-file. Decided after M1.0b: Native AOT. |
| M3 | Where the private key for signing updates is kept, and how a release is signed. |
| M1 | How the heartbeat is sent. Decided: SSE. |
| M1 | GitHub Actions with Claude (`claude-code-action`, PR review). Decided: no. The local `code-reviewer` is enough. |
| process | Plan mode at the start of a module. Decided: no. Planning stays in the conversation, with multiple-choice questions. |
| M1.7 or M6 | Whether all UI texts live in one file. Decided in M1.7: yes, `src/ui/src/texts.ts`. |
| M3 | Binding the encrypted group to the record id and the level (associated data in AES-GCM), so that a group cannot be moved to another record. This is an addition to the design. Decided for private records: the owner and the record id (design section 7.10). |
| M3 | Where and how the key for the admin seal is kept. |
| M5 | Rules for changing a template that has records. Private records are read only by their owner's exe, so the rules must work without the admin rewriting them. |
| M2 | Whether `share.json` is checked again when it is read: a folder that is no longer a network path is refused (M1.5a security review). A start of the real exe from a folder someone else controls moves the user's data there. |
| M3 | Whether the first copy checks the copied exe against the signed `version.json` before it starts it. The bytes are copied by path some seconds after the start, and a DLL planted next to the exe in the shared folder runs in it (M1.5a security review). The checkpoint after M1 confirmed it: planted copies of `NTASN1.dll` and `SSPICLI.DLL` replaced the system ones in every start from the shared folder, and more in some ([tasks/M1.md](tasks/M1.md#checkpoint-in-the-real-environment)). |
| M6 | Whether the UI shows the shared folder that Kiler uses (M1.5a security review). |
| M6 | UI component library, routing, data loading. |
| M7 | What highlights the code in MD: CodeMirror or a separate library. |
| M8 | Which language the README is in. Decided: English, like the other repository documents. |

---

## 7. Exam write-up

The document is 3–6 pages, with up to ½ page per module. That is why the plan's modules are merged there:

| Section in the document | Modules from the plan |
|---|---|
| Host and infrastructure | M1 |
| Data storage | M2 |
| Security | M3, M4 |
| Records and search | M5 |
| UI and documents | M6, M7 |
| Tests and CI | M8 |

The final grouping is decided when the document is written.

**Tool comparison.** Only Claude Code is used. The "Challenges & Tool Comparison" section compares the Claude Code features that were used (subagents, hooks, skills, CLAUDE.md, memory) and says which helped most and why. It does not compare AI products. The material comes from the tool field of each `/note` entry.

---

## 8. Agents, skills and hooks

The split is as follows:

- **Skills** carry the rules for a given technology or part. They are loaded into the main agent while it writes the code.
- **Subagents** are reviewers. They start with an empty context, so they look at the changes "with fresh eyes", by a fixed list. They do not write code.
- **Hooks** check automatically, without relying on the agent's memory.

| Name | Kind | When it is used | Role |
|---|---|---|---|
| `kiler-dotnet` | skill | C# and .NET code | project structure, Native AOT, xUnit v3 and TDD, Windows parts behind interfaces |
| `kiler-crypto` | skill | encryption, keys, passwords, tokens | the algorithms and rules from design sections 7 and 12 |
| `kiler-ui` | skill | `src/ui` | React, TypeScript, Vite, API, secret values in the browser, Vitest |
| `code-reviewer` | subagent, read-only | every task, before the tick | scope, the tests from "Tests", bugs, the rules, real data |
| `security-reviewer` | subagent, read-only | security tasks (M1.4, M3, M4, M7, etc.) | local server, permissions, secret values, encryption, paths, HTML |
| `docs-researcher` | subagent, web search | when in doubt about the API of the new versions | a short answer with sources |
| `note` | skill (command `/note`) | after a step of the work | a course note |
| `log-prompt` | hook, `UserPromptSubmit` | every message that enters the conversation | log in `course/prompts.jsonl`: the user's prompts, plus subagent reports and system notifications as a debug log |
| `data-guard` | hook, `PreToolUse`; git hooks `pre-commit`, `commit-msg` and `pre-push` (M1.0a) | `git commit` and `git push` | blocks changes with internal terms from `.claude/data-guard.local.txt` (outside git) |

Rules:

1. **Only decisions that were made.** Only what is decided goes into the skills. Deferred items are marked with the module where they are decided.
2. **Review at the start of each module.** The skills and agents are updated with the module's decisions.
3. **Watch for gaps.** If a reviewer misses something or raises a false alarm, or if a skill gives a wrong rule, the file is fixed in the same task, and the reason goes into the note with `/note`.
4. **Reviewer findings are verified.** They are not applied blindly.
5. **No internal data.** The agent and skill files are in git, so they contain no internal names.
6. **Hooks are registered in `.claude/settings.local.json`**, which is outside git, because the path to the script is local. The scripts themselves are in git.
7. **`data-guard` coverage:** the Claude hook sees only Claude's commands through the Bash and PowerShell tools. Commits from the app's buttons, another terminal or another tool are covered by the git hooks (M1.0a), once they are enabled for the clone with `git config core.hooksPath .githooks`. `git commit --no-verify` skips the git hooks, and Claude must not use it.
8. **Language:**
   - AI instructions in `.claude/` (skills, agents, hooks) are in English. This keeps the descriptions shorter and the conditions for use more precise.
   - The descriptions are short, in the form "what it is. Use when …".
   - Repository documents are in English.
   - Communication with the user and the course notes (`course/`, `/note`) are in Bulgarian.
   - The app is entirely in English: UI, messages, errors, logs, reports and demo data. This deviates from the Bulgarian examples in the design; the user decided it on 2026-10-21. See the `kiler-ui` skill.

MCP: the desktop app already provides MCP tools, for example the built-in browser for checking the UI. A project-specific `.mcp.json` is not needed for now.

---

## 9. After the exam

After the exam is graded, the public GitHub repo is deleted and the project continues in the company Bitbucket, which is private. The details are decided then.

| # | What | Note |
|---|---|---|
| 1 | Push the whole history to the company Bitbucket | a new remote |
| 2 | Delete the GitHub repo | done by the user and cannot be undone; only after the exam is graded, because the write-up links to it |
| 3 | `data-guard`: remove it, or narrow it to real secrets (passwords, keys, tokens) | internal names are allowed in the company repo |
| 4 | `KILER_DESIGN.md` goes into the repo | it was kept out only because of the internal names |
| 5 | CI: from GitHub Actions to Bitbucket Pipelines | a Windows build needs a self-hosted runner; without one, build and tests stay local, through the same scripts |
| 6 | Release: `Kiler.exe` and the signed `version.json` are copied to the shared folder | as in design section 4; the local copies update themselves (M3) |
| 7 | CLAUDE.md: remove the course parts and update the data rules | `course/` stays personal and goes nowhere |

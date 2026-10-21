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
| Platform | .NET 10, C#. The exe is self-contained single-file, `win-x64` only. |
| Server | ASP.NET Core minimal API (Kestrel) on `127.0.0.1` |
| UI | React 19, TypeScript, Vite. The finished build is embedded in the exe. |
| Code in the UI | CodeMirror 6, sql-formatter (with PL/SQL) |
| Documents | markdown-it, DOMPurify, Mermaid |
| Data | JSON files, System.Text.Json |
| Cryptography | built into .NET: AES-GCM, PBKDF2-SHA256, HMAC-SHA256, DPAPI |
| C# tests | xUnit v3 (package `xunit.v3`), the built-in assertions |
| API tests | WebApplicationFactory (`Microsoft.AspNetCore.Mvc.Testing`) |
| UI tests | Vitest |
| E2E | Playwright for .NET (`Microsoft.Playwright.Xunit.v3`) |
| Coverage | coverlet and ReportGenerator, no required threshold |
| Code style | `dotnet format`, ESLint, Prettier |
| CI | GitHub Actions, `windows-latest` |

The exact versions are fixed when the projects are created: the latest stable ones at that time. Node.js is needed only for the UI build. Users get only the exe.

---

## 3. Solution structure

```
Kiler.slnx
Directory.Build.props      shared settings: nullable, warnings, version
src/
  Kiler.Crypto/            cryptography, no dependencies
  Kiler.Storage/           files: writing, history, trash, cache, change watching
  Kiler.Core/              templates, records, search, users, permissions, audit
  Kiler.App/               Kiler.exe: host, API, the Windows implementations
  ui/                      React + Vite; the build is embedded in Kiler.App
tools/
  Kiler.DemoData/          generator of made-up demo data
tests/
  Kiler.Crypto.Tests/
  Kiler.Storage.Tests/
  Kiler.Core.Tests/
  Kiler.App.Tests/         API tests and smoke tests of the exe
  Kiler.E2E/               Playwright
docs/
  PLAN.md
.github/workflows/
  ci.yml
  release.yml
```

Dependencies:

```
Kiler.App ──► Kiler.Core ──► Kiler.Crypto
                   │
                   └───────► Kiler.Storage
```

- Kiler.Crypto and Kiler.Storage do not know about each other. Kiler.Core connects them.
- The Windows parts are interfaces in Kiler.Core: current account, DPAPI, file owner, Explorer. Their implementations are in Kiler.App. This way the core tests use fake implementations.

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

**Goal:** `Kiler.exe` is started with a double click, starts a local server and opens the browser. The same exe is used for the checks from design section 14.

**Before the steps:**

- Git hooks `pre-commit` and `pre-push` that call the `data-guard` script, so commits made outside Claude are checked too (M1.0a).
- A probe exe, `tools/Kiler.Probe`, published the same way as `Kiler.exe`, for the checks from design section 14 before the real code is written (M1.0b). It starts a local server and opens the browser, and with `--diag <folder>` it measures many small files and checks the file owner. Only a summary of its results goes into the repo.

**Steps:**

1. Solution and projects as in section 3, `Directory.Build.props`, `.editorconfig`.
2. ASP.NET Core on `127.0.0.1`, on a free port.
3. Protection of the local server:
   - the address the browser is opened with contains a random 32-byte token;
   - on the first request, the token is exchanged for an HttpOnly cookie (`SameSite=Strict`) and the address is cleared;
   - a request without a valid cookie gets 401;
   - a request with a foreign `Host` header is rejected, as protection against DNS rebinding.
4. Opening the default browser.
5. Single instance: a named mutex and a file with the port and the token in `%LOCALAPPDATA%\Kiler`. A second start only opens the browser to the running instance.
6. Shutdown: the page sends a signal that it is alive (heartbeat). If there is no signal for a few seconds, the process stops. The wait covers a page reload.
7. Parameters for tests and diagnostics: `--no-browser`, `--port`, `--data <folder>`, `--diag`.
8. UI skeleton in `src/ui`:
   - Vite, React and TypeScript;
   - a common page frame, light and dark theme, an API client;
   - during development, the Vite dev server forwards `/api` to .NET;
   - on build, MSBuild runs `npm ci` and `npm run build`, and the files are embedded as resources.
9. Publishing: `dotnet publish` produces one `Kiler.exe`.
10. Diagnostic mode `--diag` for section 14: speed of listing and reading many small files, owner of a new file. It takes over the logic of the probe, and the probe is then deleted.

**Tests:**

- API tests of the protection: without a token, with a wrong token, with a foreign `Host`.
- Smoke test: the published exe with `--no-browser --port` answers a request and stops when there is no heartbeat.
- A second start does not start a second server.

**Done:**

- a double click opens the browser with the page;
- a second start uses the running instance;
- closing the page stops the process;
- CI passes;
- the exe is ready for the checks from design section 14.

---

### M2. Data layer

Design section 5.

**Goal:** reliable reading and writing of the JSON files in the shared folder, when many people use it at the same time. The layer does not know about encryption. To it, the encrypted parts are plain strings.

**Steps:**

1. The structure of `data\` (section 5.1): `settings.json`, `levels.json`, `templates`, `users`, `records`, `history`, `trash`, `audit`. By default the folder is next to the exe, and `--data` changes it.
2. Models and serialization with System.Text.Json and source generation.
3. Atomic write (section 5.4): a temporary file, a copy of the old version in `history\<id>\vN.json`, rename.
4. Versions and conflicts (section 5.5): write only if the version on disk is the expected one. Otherwise the result is "conflict" with the version from disk.
5. Deletion to `trash\` and restore from there. A list of the versions and restoring an old version (section 5.6).
6. Loading and local cache (section 5.3): startup is from the copy in `%LOCALAPPDATA%\Kiler`, after which only the changed files are synchronized.
7. Watching for changes about every 10 seconds: one listing of the folder with names and dates, reloading the changed files, events to the upper layers.
8. Resilience:
   - a corrupted JSON file is skipped and reported, without a crash;
   - when the network is unavailable, the application works from the cache and shows this.
9. File owner (section 7.8) as an interface, with a Windows implementation in Kiler.App.

**Tests** (the tests are written first):

- atomic write with a simulated crash in the middle: the original stays intact;
- version conflict;
- history and trash;
- detecting a new, changed and deleted file;
- cache synchronization;
- corrupted file;
- loading 2000 files, with timing.

**Done:** a C# API for reading, writing, deleting, history and watching, covered by tests. The layer has no UI.

---

### M3. Cryptography

Design sections 7.1–7.3, 7.5 and 7.7.

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

**Tests** (the tests are written first):

- encrypting and decrypting gives the same text;
- a change of one byte is detected;
- a wrong password does not unlock the keys;
- verification with `check`;
- changing the password keeps the same level key;
- recovery with the recovery key;
- known test vectors for PBKDF2 and AES-GCM;
- an old version of the format is read.

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
3. Sign-in (section 7.5): finding the file by the Windows account, password, checking the keys with `check`. A temporary password must be changed.
4. Guest mode: level 0, read-only, a "Sign in" button.
5. Session in the exe's memory:
   - keeps the unlocked keys;
   - automatic locking, overall and per level (for example Prod);
   - locking removes the keys and the decrypted data from memory.
6. "Remember me":
   - only for levels without `"remember": false`;
   - Prod always asks for a password;
   - a single level is unlocked from the padlock.
7. Admin actions (section 7.6): new user, level change, new temporary password, "editor" and "admin" rights. Every user file gets a seal.
8. Detection (section 7.8):
   - users without a valid seal are marked as suspicious;
   - changes by people without `canEdit` are detected by the file owner.
9. Permission check on the server on every request: read level, `canEdit`, `isAdmin`. Nobody writes a field above their level (section 7.4).
10. Audit (sections 8.2 and 12): showing and copying Prod passwords is recorded in `audit\<date>\<user>.jsonl`.
11. Removing an untrusted person (section 7.9):
    - a new key for the level;
    - all groups with this level are encrypted again;
    - the other users with this level get new temporary passwords.
12. Screens:
    - the wizard;
    - sign-in and password change;
    - locked screen and unlocking a level;
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
- E2E: wizard → sign-in → the admin creates a user.

**Done:** all roles from section 7.4 work through the API and the screens, and permissions are checked on the server.

---

### M5. Templates, records and search

Design sections 5.2, 6 and 11.

**Goal:** the record logic and the API for the UI.

**Steps:**

1. Template model and validation: field types (section 6.4) and their settings (section 6.5), `levelByEnv`, `ref`, selectable language.
2. The initial templates (section 6.1): "Server / database", "SQL query", "Link", "Note".
3. Record:
   - common fields: system, function, title, tags;
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
   - a System → Function → Type tree with counters;
   - everything is updated on changes from M2.
8. `ref`: the title of the record it points to and a link to it. Handling the case when that record is deleted.
9. API:
   - search and reading a record;
   - creating and editing with a version (on conflict: 409 with both versions);
   - deleting, restoring from the trash, history;
   - templates (for admin);
   - suggestions for systems and general settings.
10. Changing a template that has records (a removed field, a changed level): rules and re-encryption where needed.

**Tests:**

- search in Cyrillic and in Latin;
- passwords are never found;
- `levelByEnv`;
- a partial edit keeps the group above the editor's level;
- an editor cannot change the environment so that the password is raised above their level;
- conflict;
- `ref`;
- 2000 records, with timing;
- API tests of all endpoints, including the permissions.

**Done:** the full API for records and templates, covered by tests.

---

### M6. User interface

Design sections 8 and 10.

**Goal:** the main view and all the work with records.

**Steps:**

1. Main view:
   - a tree with counters and filters;
   - global search with Ctrl+K and a delay of about 150 ms;
   - a list with the results (the fields with `list`).
2. Record view:
   - "copy" buttons;
   - passwords: hidden, with "show" and "copy", which get the value from the server;
   - padlocks with an unlock button.
3. Forms generated from the templates: creating and editing, required fields, raising the level, choosing a `ref`, suggestions for the system, tags.
4. Code (section 8.3):
   - CodeMirror 6 with the languages from the design, line numbers, search, themes;
   - "Copy" and "Format" buttons (SQL, including PL/SQL);
   - a "Parameters" button: fields for the bind variables and "Copy filled in".
5. Conflict (section 5.5): the two versions side by side and a choice.
6. History and trash (section 5.6): who changed what and when; restore with one click.
7. Admin screens (section 8.4):
   - templates;
   - general settings: guest mode, path variables, locking;
   - history and audit.
8. Personal configuration (section 10): theme (light, dark or system), the values of the path variables, "remember me". It is kept in `%APPDATA%\Kiler\local-settings.json`.
9. Updating on changes from other users.

`markdown` fields are shown as text until M7.

**Tests:**

- Vitest: detecting the bind parameters, filling in a query, generating a form from a template.
- E2E: search and filters; creating and editing; showing and copying a password with audit; conflict; theme.
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
   - cache for NuGet, npm and the Playwright browsers;
   - `npm ci`, ESLint, type check, Vitest, UI build;
   - `dotnet format --verify-no-changes`;
   - build, unit and API tests with coverage;
   - publish and smoke test of the exe;
   - E2E;
   - artifacts: the exe, the coverage report, the traces on failure, the screenshots.
3. ~~`release.yml`: on a `v*` tag, a publish is made, plus a GitHub Release with `Kiler.exe` and its SHA256 checksum.~~ Dropped: a GitHub Release is useful only while the public repo exists. The CI artifact is enough for the exam, and in the company the exe is copied to the shared folder.
4. `tools/Kiler.DemoData`. It uses the Kiler libraries, so it encrypts correctly. It grows with M4–M7. It has two modes:
   - a made-up set of about 30 records: several systems, all templates, levels 0–3, an admin and two users with demo passwords;
   - a "volume" mode with 2000 records.
5. Screenshots for the exam write-up from the E2E tests, with the demo data.
6. README for the public repo: what Kiler is, screenshots, how to build it and how to run the demo.

---

## 6. Open questions

They are resolved at the start of the relevant module. The decisions are also recorded in the module's task file.

| When | Question |
|---|---|
| after M1 | The checks from design section 14 with the exe from M1. Decided: there is a `--diag` mode for them (M1.11). |
| M1 | Native AOT or plain single-file. Decided: single-file with AOT-ready code. Native AOT is decided after the checks from section 14. |
| M1 | How the heartbeat is sent. Decided: SSE. |
| M1 | GitHub Actions with Claude (`claude-code-action`, PR review). Decided: no. The local `code-reviewer` is enough. |
| process | Plan mode at the start of a module. Decided: no. Planning stays in the conversation, with multiple-choice questions. |
| M1.7 or M6 | Whether all UI texts live in one file. |
| M3 | Binding the encrypted group to the record id and the level (associated data in AES-GCM), so that a group cannot be moved to another record. This is an addition to the design. |
| M3 | Where and how the key for the admin seal is kept. |
| M5 | Rules for changing a template that has records. |
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
| `kiler-dotnet` | skill | C# and .NET code | project structure, AOT-ready code, xUnit v3 and TDD, Windows parts behind interfaces |
| `kiler-crypto` | skill | encryption, keys, passwords, tokens | the algorithms and rules from design sections 7 and 12 |
| `kiler-ui` | skill | `src/ui` | React, TypeScript, Vite, API, secret values in the browser, Vitest |
| `code-reviewer` | subagent, read-only | every task, before the tick | scope, the tests from "Tests", bugs, the rules, real data |
| `security-reviewer` | subagent, read-only | security tasks (M1.4, M3, M4, M7, etc.) | local server, permissions, secret values, encryption, paths, HTML |
| `docs-researcher` | subagent, web search | when in doubt about the API of the new versions | a short answer with sources |
| `note` | skill (command `/note`) | after a step of the work | a course note |
| `log-prompt` | hook, `UserPromptSubmit` | every message that enters the conversation | log in `course/prompts.jsonl`: the user's prompts, plus subagent reports and system notifications as a debug log |
| `data-guard` | hook, `PreToolUse`; git hooks `pre-commit` and `pre-push` (M1.0a) | `git commit` and `git push` | blocks changes with internal terms from `.claude/data-guard.local.txt` (outside git) |

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
| 6 | Release: `Kiler.exe` is copied to the shared folder | as in design section 4 |
| 7 | CLAUDE.md: remove the course parts and update the data rules | `course/` stays personal and goes nowhere |

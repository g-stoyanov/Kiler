# Kiler

A portable organizer for the team: database and server addresses, accounts, SQL queries, links to documentation, grouped in a tree of up to four levels (stashes). One self-contained .NET exe starts a local web server on `127.0.0.1` and opens the browser. The data is JSON files in a shared folder, and the sensitive fields are encrypted by access level.

The full design is in `KILER_DESIGN.md`. It is the source of truth for the decisions: do not rediscover them, and if the code has to deviate from it, ask first.

## Course project

Kiler is also the exam project for the AI-Assisted Development course (SoftUni). The assignment is in `../Project-Assignment.docx`, and the presentations are in `../*.pdf`.

The deliverable is a Google Doc of 3–6 pages: idea, modules and for each module the approach, steps, testing, AI tool and why, 2–3 key prompts. Plus challenges, at least 2 screenshots and a link to GitHub.

- **Scope:** the whole application is implemented according to `KILER_DESIGN.md`. The work plan is complete and accurate, by the real modules. Only in the exam write-up can modules be merged or shortened to fit the length limit.
- **Plan:** [docs/PLAN.md](docs/PLAN.md). The modules are done in this order:
  1. Host and skeleton
  2. Data layer
  3. Cryptography
  4. Users and permissions
  5. Templates, records and search
  6. User interface
  7. Documents and diagrams
  8. Tests and automation (throughout the project)
- **Phases:**
  1. Rough refinement: `KILER_DESIGN.md`. Done.
  2. Plan: `docs/PLAN.md`, `docs/tasks/`, GitHub repo, development environment. Done.
  3. **Development (current):** modules M1–M7, while M8 runs the whole time. The checks in the real environment (design section 14) were a checkpoint right after M1, with the instructions in [docs/real-environment-checks.md](docs/real-environment-checks.md); the results are in [docs/tasks/M1.md](docs/tasks/M1.md#checkpoint-in-the-real-environment). Until the exam, the steps in [docs/PLAN.md, section 10](docs/PLAN.md#10-deferred-until-after-the-exam) are deferred, M7 among them, and the tasks follow the [light cycle](docs/tasks/README.md#light-cycle-until-the-exam).
  4. Stabilization and release: full E2E with demo data, README, final screenshots, release `v1.0`, trial use in the real environment with the checks of the data layer in [docs/real-environment-checks.md](docs/real-environment-checks.md#the-trial-use-phase-4). The trial use comes after the exam, after the first three steps of PLAN section 10.
  5. Exam write-up: Google Doc from `course/NOTES.md` and `course/PROMPTS.md`.
  6. After the exam: move to the company Bitbucket and delete the GitHub repo. The checklist is in [docs/PLAN.md, section 9](docs/PLAN.md#9-after-the-exam).

  The user tracks the exam deadline. The work follows the plan in this order.
- **Tool:** Claude Code only. The write-up compares the Claude Code features that were used (subagents, hooks, skills, etc.), not AI products.

## Course notes (`course/`)

| File | What | Who writes it |
|---|---|---|
| `course/prompts.jsonl` | every message that enters the conversation: the user's prompts, but also subagent reports and system notifications. It doubles as a debug log of what was called. | the hook `.claude/hooks/log-prompt.ps1`, automatically. Do not edit by hand. |
| `course/NOTES.md` | raw notes by module and phase | the skill `/note` |
| `course/PROMPTS.md` | selected key prompts by phase | by hand, at the end of a phase. In phase 3 it is filled in at the end of each module. |

When a step of the work is finished (a module, part of a module, a plan), remind the user to run `/note`.

## Data and the public repo

The repo is public on GitHub: https://github.com/g-stoyanov/Kiler. Real data does not go into the code, tests, demo data or commits: names of internal systems, hosts, accounts, passwords, paths to company storage. The demo data is made up.

These are outside git (in `.gitignore`):

- `KILER_DESIGN.md`, because it contains internal names;
- `course/`;
- `.claude/settings.local.json`.

## Working style

- Communicate with the user in Bulgarian.
- Before a new module: its open questions and the tasks in `docs/tasks/`, then code.
- Questions are answered in the chat. Files are created only when explicitly asked.
- Commit and push only when explicitly asked.
- Commit messages are in English.
- Repository documents and AI instructions in `.claude/` (skills, agents, hooks) are in English, with short "Use when …" descriptions. Course notes in `course/` (not in git) and `/note` entries are in Bulgarian.

## Tasks

Work follows the tasks in `docs/tasks/`, one file per module, with numbers `M<module>.<n>` and ticks. The done tasks, with all their notes and review passes, are in `docs/tasks/log/`, which is read only when a note points there. Before working on code, read [docs/tasks/README.md](docs/tasks/README.md). It contains:

- the task format;
- the work cycle;
- what is done at the start and at the end of a module.

One session covers one or several small tasks from one module.

## Agents, skills and hooks

They are described in [docs/PLAN.md, section 8](docs/PLAN.md#8-agents-skills-and-hooks).

- **Skills with the rules:** `kiler-dotnet`, `kiler-crypto`, `kiler-ui`. `kiler-dotnet` keeps the rules of one area in a file of its own (`host.md`, `storage.md`), read only for that area.
- **Reviewer subagents:** `code-reviewer` for every task and `security-reviewer` for the security tasks. They only read.
- **Documentation subagent:** `docs-researcher`.
- **Hooks:**
  - `log-prompt` records the prompts;
  - `data-guard` blocks commit and push with internal terms. The terms are in `.claude/data-guard.local.txt`, which is outside git. When the hook changes, run its tests: `.claude/hooks/data-guard.tests.ps1`. The git hooks in `.githooks/` call the same script, so commits made outside Claude are checked too: `pre-commit` and `commit-msg` check `git commit`, and `pre-push` checks every pushed commit, however it was made. They are turned on once per clone with `git config core.hooksPath .githooks`. Claude does not use `--no-verify`, which skips them.

  The hooks are registered in `.claude/settings.local.json`.

## Code

The details are in [docs/PLAN.md](docs/PLAN.md), sections 2 and 3.

- **Stack:** .NET 10, ASP.NET Core minimal API, React 19 with TypeScript and Vite. `win-x64` only.
- **Solution:** `Kiler.slnx`.
  - `src/`: Kiler.Crypto, Kiler.Storage, Kiler.Core, Kiler.App (produces `Kiler.exe`) and `ui/` with React.
  - `tools/Kiler.DemoData`.
  - `tests/`.
- **The Windows parts** (account, DPAPI, Explorer) are interfaces in Kiler.Core, and their implementations are in Kiler.App. The exceptions are in Kiler.Storage (PLAN section 3): the file owner is an interface there, and it opens the files and folders of the shared folder as themselves through Windows (M2.3).
- **Tests:** xUnit v3, API tests on the real Kestrel, Vitest, Playwright for .NET. For Kiler.Crypto and Kiler.Storage the tests are written first (TDD).
- **CI:** GitHub Actions on `windows-latest`.
- **Build and test commands.** The execution policy on the developer machine is AllSigned, so the scripts run through `-ExecutionPolicy Bypass`, and in PowerShell npm is `npm.cmd`, because `npm.ps1` is not signed. They need Node.js 24.15 or newer, and the publish needs the Visual Studio C++ tools. CI runs the first six, in this order.
  - Format check: `powershell -ExecutionPolicy Bypass -File .\format.ps1`. `dotnet format Kiler.slnx` fixes the files.
  - UI checks (Prettier, ESLint, type check, Vitest): `powershell -ExecutionPolicy Bypass -File .\check-ui.ps1`. It runs `npm ci` first when `src\ui\node_modules` is missing or older than `package-lock.json`.
  - Build, Release by default: `powershell -ExecutionPolicy Bypass -File .\build.ps1`. A Release build also builds the UI and embeds it; a Debug build (`-Configuration Debug`) has no UI.
  - All tests with coverage: `powershell -ExecutionPolicy Bypass -File .\test.ps1`, with `-NoBuild` after `build.ps1`. The report is in `TestResults\coverage-report`. The smoke tests show as skipped here, and so does the test that runs only in Debug.
  - Publish: `powershell -ExecutionPolicy Bypass -File .\publish.ps1`. `Kiler.exe` alone in `artifacts\publish`, its PDB in `artifacts\symbols`.
  - Smoke tests of the published exe: `powershell -ExecutionPolicy Bypass -File .\smoke.ps1`. They run the exe in `artifacts\publish` as it is, so publish again after a change of the code. `-Exe <path>` runs them against another exe, such as the CI artifact.
  - One test project, or some tests: `dotnet test --project tests\Kiler.App.Tests -- --filter-method "*Name*"`, or `--filter-class`. It builds Debug, so the tests of the embedded UI skip.
  - One UI check, in `src\ui`: `npm.cmd run <script>` with `format:check`, `lint` or `typecheck`, and `npm.cmd test` for Vitest. `npm.cmd run format` fixes the format.
  - UI dev mode, in `src\ui`, after `check-ui.ps1` has installed the packages: `npm.cmd run dev`. It starts Kiler with `dotnet run` and the data folder `dev-data\`, then the Vite dev server with a proxy to Kiler, and opens the browser. When the last tab closes, Kiler stops after the idle wait, and the dev server with it. There is no HMR, so reload the page after a change of the UI; after a change of the C# code, stop it and start it again. Claude runs `npm.cmd run dev -- --no-browser` in the background: it prints the address with the token instead of opening a browser, and without a page that holds `/api/events`, Kiler stops 60 s after the start. The details are in the `kiler-ui` skill.

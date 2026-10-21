# Kiler

A portable organizer for the team: database and server addresses, accounts, SQL queries, links to documentation, grouped by system, function and type. One self-contained .NET exe starts a local web server on `127.0.0.1` and opens the browser. The data is JSON files in a shared folder, and the sensitive fields are encrypted by access level.

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
  3. **Development (current):** modules M1–M7, while M8 runs the whole time. The checks in the real environment (design section 14) are a checkpoint right after M1.
  4. Stabilization and release: full E2E with demo data, README, final screenshots, release `v1.0`, trial use in the real environment.
  5. Exam write-up: Google Doc from `course/NOTES.md` and `course/PROMPTS.md`.
- **Tool:** Claude Code.

## Course notes (`course/`)

| File | What | Who writes it |
|---|---|---|
| `course/prompts.jsonl` | every prompt sent | the hook `.claude/hooks/log-prompt.ps1`, automatically. Do not edit by hand. |
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

Work follows the tasks in `docs/tasks/`, one file per module, with numbers `M<module>.<n>` and ticks. Before working on code, read [docs/tasks/README.md](docs/tasks/README.md). It contains:

- the task format;
- the work cycle;
- what is done at the start and at the end of a module.

One session covers one or several small tasks from one module.

## Agents, skills and hooks

They are described in [docs/PLAN.md, section 8](docs/PLAN.md#8-agents-skills-and-hooks).

- **Skills with the rules:** `kiler-dotnet`, `kiler-crypto`, `kiler-ui`.
- **Reviewer subagents:** `code-reviewer` for every task and `security-reviewer` for the security tasks. They only read.
- **Documentation subagent:** `docs-researcher`.
- **Hooks:**
  - `log-prompt` records the prompts;
  - `data-guard` blocks commit and push with internal terms. The terms are in `.claude/data-guard.local.txt`, which is outside git. When the hook changes, run its tests: `.claude/hooks/data-guard.tests.ps1`.

  The hooks are registered in `.claude/settings.local.json`.

## Code

The details are in [docs/PLAN.md](docs/PLAN.md), sections 2 and 3.

- **Stack:** .NET 10, ASP.NET Core minimal API, React 19 with TypeScript and Vite. `win-x64` only.
- **Solution:** `Kiler.slnx`.
  - `src/`: Kiler.Crypto, Kiler.Storage, Kiler.Core, Kiler.App (produces `Kiler.exe`) and `ui/` with React.
  - `tools/Kiler.DemoData`.
  - `tests/`.
- **The Windows parts** (account, DPAPI, file owner, Explorer) are interfaces in Kiler.Core, and their implementations are in Kiler.App.
- **Tests:** xUnit v3, WebApplicationFactory, Vitest, Playwright for .NET. For Kiler.Crypto and Kiler.Storage the tests are written first (TDD).
- **CI:** GitHub Actions on `windows-latest`.
- **Build and test commands:** recorded here in M1 (task M1.12), once the projects exist.

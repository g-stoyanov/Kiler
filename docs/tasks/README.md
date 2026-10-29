# Development tasks

Here [the plan](../PLAN.md) is broken into small tasks with ticks. The work follows them, and progress is tracked by them.

## Modules

| Module | Tasks | Status |
|---|---|---|
| M1 Host and skeleton | [M1.md](M1.md) | done, with the checkpoint in the real environment |
| M2 Data layer | [M2.md](M2.md) | done; its checks that need the user's hands go to the [trial use](../real-environment-checks.md#the-trial-use-phase-4) |
| M3 Cryptography | [M3.md](M3.md) | done; part of it deferred until after the exam ([PLAN.md, section 10](../PLAN.md#10-deferred-until-after-the-exam)) |
| M4 Users and permissions | [M4.md](M4.md) | in progress; part of it deferred until after the exam ([PLAN.md, section 10](../PLAN.md#10-deferred-until-after-the-exam)) |
| M5 Templates, records and search | | not started |
| M6 User interface | | not started |
| M7 Documents and diagrams | | deferred until after the exam ([PLAN.md, section 10](../PLAN.md#10-deferred-until-after-the-exam)) |

M8 (tests and automation) has no file of its own. Its tasks go into the modules when they are needed:

- the test projects and CI are in M1;
- the demo data grows from M4 onward;
- the README is at the end.

---

## What a task is

- It fits in one session, with its reviews, and ends with a working build. That makes one commit.
- **Tests are part of the task.** They are not split off into a "tests" task at the end.
- It has a number `M<module>.<n>` with a Latin M, for example `M2.3`.
- A task that does not fit in one session is split.

## Format

```markdown
- [ ] **M1.4 Token and cookie**
  - What: exchange of the token from the address for an HttpOnly cookie; Host check
  - Tests (first): no token → 401; wrong token → 401; foreign Host → 400
  - Done: the tests pass; the browser opens the page
```

- **What:** the scope of the task, with a reference to the plan step if needed.
- **Tests:** which tests are written. "(first)" means they are written before the code (TDD).
- **Done:** how to check that the task is finished.

A finished task becomes `- [x]`. If something turned out different from the description, a line `- Note: ...` is added under the task. Its whole entry then moves to the log (step 6 of the cycle).

## Files

- `M<n>.md`: the module's decisions, the title lines of the done tasks, the open tasks, and "For later modules". An open task carries the notes of the earlier tasks that it needs, under "From earlier tasks". This is what a session reads.
- `log/M<n>.md`: the done tasks whole, with all their notes: decisions, measurements, review passes and fixes. It is read only when a note points there, such as "the M2.5 note", or when the history of a decision is needed; search it by the task number.

The numbers do not change. A new task between two existing ones gets a letter, for example `M1.4a`.

A dropped task stays in the list so the numbers stay stable. It is ticked and struck through, `- [x] ~~**M1.10 …**~~ Dropped.`, with a note that says why. The work cycle skips it.

---

## Work cycle (one task)

1. **Pick.** Read this file and, in the file of the current module, the decisions at the top and the task. Take the first unticked task, unless the user names another one. The notes of the earlier tasks that it needs are under it, in "From earlier tasks"; follow them with the task. The other tasks are not needed. If the task has an "Open after review pass 2" note, continue from there: the state that the pass reviewed is already staged, so fix the findings, then run the next pass (step 5).
2. **Unclear points.** If the task is unclear or needs a decision that is not in the plan, ask before the code.

   **Size.** A task fits in one session with its reviews. If its "What" holds more than about three separate pieces, or more than one new mechanism, propose a split into lettered tasks before the code. M2.7a did not fit: five review passes, and the context of the session almost full.
3. **Tests.** In Kiler.Crypto and Kiler.Storage the tests are written first and must fail. Elsewhere they are written in the same task, after the code.
4. **Code.** Follow the rules of the skill for the part: `kiler-dotnet`, `kiler-ui` or `kiler-crypto` ([PLAN.md, section 8](../PLAN.md#8-agents-skills-and-hooks)). `kiler-dotnet` has a file per area (`host.md`, `storage.md`); read only the one of the project that the task changes. A large source file is read by its outline first (the signatures, with Grep), then the parts the task needs. When in doubt about the API of the new versions, ask `docs-researcher`. A new package or tool always gets its current stable version and its setup from `docs-researcher`, not from memory: the model's knowledge can be out of date (M1.1: xUnit v3 runs on Microsoft Testing Platform).
5. **Check:**
   - **the checks**, once before the first review and once more before the commit, after the last fixes:
     - the full check, `format.ps1`, `build.ps1` and `test.ps1 -NoBuild` (the commands are in CLAUDE.md), only when the task changes more than one project, what another project uses, the build, the scripts, CI or `src/ui`; `check-ui.ps1` when `src/ui` changed; `publish.ps1` and `smoke.ps1` when the task changes what only the published exe shows (the host, the start, the publish);
     - otherwise, for a task that changes one project and its tests (and docs), `format.ps1` and the tests of that project (`dotnet test --project …`). CI runs the full check on every push (step 7).

     Between the review passes, and after any fix, only the tests of the changed projects run (`dotnet test --project …`, and `check-ui.ps1` when `src/ui` changed), with `format.ps1`, never the full check. Decided by the user after M2.9b, which ran the full check twice and the whole Kiler.Storage tests ten times for a fix of 16 lines;
   - no real names, hosts, accounts or passwords in the code, tests and data. The known internal terms are checked by the `data-guard` hooks at every commit and push;
   - **review by `code-reviewer`**. For security tasks (tokens, sign-in, permissions, encryption, passwords, paths, HTML, the local server and what reaches it, the browser code that handles the cookie or the event stream) the review is also done by `security-reviewer`.

   The reviewers only read: they run no build and no test. Their brief says which checks ran and how they ended, and does not ask them to run anything (M2.4).

   Findings are verified before they are fixed, not applied blindly. If a reviewer misses something, raises a false alarm, or a skill gives a wrong rule, the agent or skill file is fixed in the same task.

   **Review passes.** A pass after fixes reviews only the fixes:
   - right after the report of a pass, before any fix, what it reviewed is staged: `git add -A`, with no commit. This holds also when the fixes are left to the next session. The brief of the next pass says that it is a repeat pass and lists the findings it checks; the reviewer takes the fixes from `git diff` and the new files;
   - only a reviewer whose pass found a critical or major point does another pass. The main agent checks the fixes of minor points itself;
   - at most two passes of each reviewer in one session. If the second pass still finds critical or major points, they are verified and written under the task as "Open after review pass 2": file, line, the finding and the verdict. The work then goes on in a new session with the short prompt of step 9, which fixes them and runs the next pass;
   - `security-reviewer` judges by the threat model of design 7.8 in its agent file. A deliberate forgery outside the model is `[minor, out of model]` and gets no fix, unless the user decides otherwise.
6. **Tick** in `M<n>.md`, plus a note if there is a deviation. If the deviation changes a decision, [PLAN.md](../PLAN.md) is also updated.

   **Record for later sessions**, without being asked. Whatever a later session would need and could not find in the docs goes where that session will read it. That can be a decision, a check, a pitfall or a quirk of a tool. Each fact is written once, in the one place where it is read:
   - a note for one later task of the module goes under that task, in "From earlier tasks", as `(M2.7a) …`;
   - a note for a later module goes under "For later modules" at the end of `M<n>.md`. A note that both need goes under the task, and "For later modules" points to it in one line; when that task is done, the note moves to "For later modules";
   - a rule for more tasks goes into the skill (into the file of its area), the agent, CLAUDE.md or this cycle. A note points to it instead of repeating it.

   The test: a session that gets only the short prompt of step 9 must know it.

   **Move the task to the log.** Then the whole entry of the done task, with all its notes, goes to the end of `log/M<n>.md`, and `M<n>.md` keeps only its title line, ticked. Its "From earlier tasks" lines are left out, as the log has them under the tasks they come from. Its history (the review passes, the fixes, the measurements) stays only in the log, and the commit message carries a short summary of it.
7. **Commit**, only when the user asks for it. The message is in English and starts with the number: `M1.4: Exchange URL token for session cookie`. The tick goes into the same commit as the code. The index holds the state of the last review pass, so stage again before the commit: `git status` first, to see that nothing outside the task is there, then `git add -A`. The `data-guard` hook blocks a commit or push with internal terms. After a push, which also happens only when the user asks, the CI run is watched to its end (`gh run watch` in the background) and its result is reported. A failure is fixed before the next task.
8. **Reminder** about `/note`.
9. **Next step:** a new task or a new session. A new session is better when the context has grown heavy or the module changes. The prompt of a new session names only the task and this cycle, for example "continue with task M1.7 from docs/tasks/M1.md, following the cycle in docs/tasks/README.md". Everything else must already be in the docs: what a session would need beyond the prompt goes into a note under the task, a skill or this cycle, not into the prompt.

## Light cycle (until the exam)

Decided by the user on 2026-10-29, with the steps deferred until after the exam ([PLAN.md, section 10](../PLAN.md#10-deferred-until-after-the-exam)). Until the exam, the reviews of step 5 go so; the rest of the cycle stays.

- `code-reviewer` makes one pass of each task. `security-reviewer` makes one pass, and only of a task of cryptography, sign-in, permissions or the secret values (the code that unlocks, shows or copies them).
- There is no repeat pass. The main agent verifies the findings, fixes the critical and major ones whose fix stays within the task, and checks the fixes itself, with the tests of the changed projects.
- A minor finding, and a finding whose fix needs a new mechanism, a decision or a task of its own, is not fixed. It goes under "Left for after the exam" at the end of the module's task file, with the task, the file and line, the finding, its severity and the verdict. A critical finding is left so only with the user's OK.
- No review finding becomes a new lettered task before the exam.

## Parallel sessions

A piece of work that needs nothing of a task under way can run in a session of its own, at the same time as the main one. Until the exam this is the UI of M6, once the first task of M5 has fixed the API (PLAN.md, M5). The rules:

- The session works in a git worktree of its own, on a branch of its own, never in the main working tree: two builds in one folder fight over `bin\` and `obj\`, and the `git add -A` of step 7 would take the files of the other session.
- It changes only its own project or folder, such as `src/ui`, and the main session leaves that folder alone while it runs. The shared files are changed only by the main session: `docs/`, `Kiler.slnx`, `Directory.Packages.props`, `.github/` and the startup of Kiler.App. The parallel session puts its ticks and notes into its final report, and the main session writes them into the docs when it merges the branch.
- `.claude/settings.local.json` is outside git, so a new worktree has no hooks of Claude. Copy it into the `.claude\` of the worktree before the work: its paths point to the scripts of the main working tree, so `log-prompt` writes to the `course/` there. The git hooks need nothing: `core.hooksPath` is shared, and `data-guard` reads the terms file of the main working tree.
- `KILER_DESIGN.md` and `course/` are not in a worktree either. Read the design in the main working tree, the parent folder of `git rev-parse --path-format=absolute --git-common-dir`.
- Two sessions that build and test at once slow each other down. A test that fails on a time limit runs again alone before it counts as a failure.

## Start of a module

1. The module's open questions from [PLAN.md, section 6](../PLAN.md#6-open-questions) are resolved.
2. The notes for the module are collected from "For later modules" at the end of the earlier module files: search them for the module number, for example `M4`. Each goes under the module's decisions or under its task. Until the exam, a note that serves only a step deferred in [PLAN.md, section 10](../PLAN.md#10-deferred-until-after-the-exam) stays where it is.
3. The skills and agents for the module are reviewed. The decisions that were deferred until now go into them. A project that gets its first code gets its area file in `kiler-dotnet`.
4. From the module's steps in PLAN.md, `M<n>.md` is written with ordered tasks, each small enough for one session with its reviews (step 2 of the cycle, "Size"). Until the exam, the deferred steps get no tasks. Its log, `log/M<n>.md`, starts empty.
5. The status in the table above becomes "in progress".

## End of a module

1. All tasks are ticked, and the module's "Done" criteria in PLAN.md are met.
2. The status becomes "done".
3. `/note` is run for the module, and its section in `course/PROMPTS.md` is filled in. The note also includes the fixes to the agents and skills made during the module.
4. The next module starts with "Start of a module".

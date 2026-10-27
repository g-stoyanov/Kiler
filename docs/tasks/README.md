# Development tasks

Here [the plan](../PLAN.md) is broken into small tasks with ticks. The work follows them, and progress is tracked by them.

## Modules

| Module | Tasks | Status |
|---|---|---|
| M1 Host and skeleton | [M1.md](M1.md) | done, with the checkpoint in the real environment |
| M2 Data layer | [M2.md](M2.md) | in progress |
| M3 Cryptography | | not started |
| M4 Users and permissions | | not started |
| M5 Templates, records and search | | not started |
| M6 User interface | | not started |
| M7 Documents and diagrams | | not started |

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
   - **the full check**, once before the first review and once more before the commit, after the last fixes: `format.ps1`, `build.ps1` and `test.ps1 -NoBuild` (the commands are in CLAUDE.md); `check-ui.ps1` when `src/ui` changed; `publish.ps1` and `smoke.ps1` when the task changes what only the published exe shows (the host, the start, the publish). Between the review passes only the tests of the changed projects run (`dotnet test --project …`, and `check-ui.ps1` when `src/ui` changed), with `format.ps1`;
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

## Start of a module

1. The module's open questions from [PLAN.md, section 6](../PLAN.md#6-open-questions) are resolved.
2. The notes for the module are collected from "For later modules" at the end of the earlier module files: search them for the module number, for example `M4`. Each goes under the module's decisions or under its task.
3. The skills and agents for the module are reviewed. The decisions that were deferred until now go into them. A project that gets its first code gets its area file in `kiler-dotnet`.
4. From the module's steps in PLAN.md, `M<n>.md` is written with ordered tasks, each small enough for one session with its reviews (step 2 of the cycle, "Size"). Its log, `log/M<n>.md`, starts empty.
5. The status in the table above becomes "in progress".

## End of a module

1. All tasks are ticked, and the module's "Done" criteria in PLAN.md are met.
2. The status becomes "done".
3. `/note` is run for the module, and its section in `course/PROMPTS.md` is filled in. The note also includes the fixes to the agents and skills made during the module.
4. The next module starts with "Start of a module".

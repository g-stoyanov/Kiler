# Development tasks

Here [the plan](../PLAN.md) is broken into small tasks with ticks. The work follows them, and progress is tracked by them.

## Modules

| Module | Tasks | Status |
|---|---|---|
| M1 Host and skeleton | [M1.md](M1.md) | in progress |
| M2 Data layer | | not started |
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

- It fits in one session and ends with a working build. That makes one commit.
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

A finished task becomes `- [x]`. If something turned out different from the description, a line `- Note: ...` is added under the task.

The numbers do not change. A new task between two existing ones gets a letter, for example `M1.4a`.

A dropped task stays in the list so the numbers stay stable. It is ticked and struck through, `- [x] ~~**M1.10 …**~~ Dropped.`, with a note that says why. The work cycle skips it.

---

## Work cycle (one task)

1. **Pick.** Read this file and the file of the current module. Take the first unticked task, unless the user names another one.
2. **Unclear points.** If the task is unclear or needs a decision that is not in the plan, ask before the code.
3. **Tests.** In Kiler.Crypto and Kiler.Storage the tests are written first and must fail. Elsewhere they are written in the same task, after the code.
4. **Code.** Follow the rules of the skill for the part: `kiler-dotnet`, `kiler-ui` or `kiler-crypto` ([PLAN.md, section 8](../PLAN.md#8-agents-skills-and-hooks)). When in doubt about the API of the new versions, ask `docs-researcher`.
5. **Check:**
   - build;
   - all tests;
   - `dotnet format --verify-no-changes`;
   - for the UI: lint and type check;
   - no real names, hosts, accounts or passwords in the code, tests and data;
   - **review by `code-reviewer`**. For security tasks (tokens, sign-in, permissions, encryption, passwords, paths, HTML) the review is also done by `security-reviewer`.

   Findings are verified before they are fixed, not applied blindly. If a reviewer misses something, raises a false alarm, or a skill gives a wrong rule, the agent or skill file is fixed in the same task.
6. **Tick** in `M<n>.md`, plus a note if there is a deviation. If the deviation changes a decision, [PLAN.md](../PLAN.md) is also updated.
7. **Commit**, only when the user asks for it. The message is in English and starts with the number: `M1.4: Exchange URL token for session cookie`. The tick goes into the same commit as the code. The `data-guard` hook blocks a commit or push with internal terms.
8. **Reminder** about `/note`.
9. **Next step:** a new task or a new session. A new session is better when the context has grown heavy or the module changes.

## Start of a module

1. The module's open questions from [PLAN.md, section 6](../PLAN.md#6-open-questions) are resolved.
2. The skills and agents for the module are reviewed. The decisions that were deferred until now go into them.
3. From the module's steps in PLAN.md, `M<n>.md` is written with ordered tasks.
4. The status in the table above becomes "in progress".

## End of a module

1. All tasks are ticked, and the module's "Done" criteria in PLAN.md are met.
2. The status becomes "done".
3. `/note` is run for the module, and its section in `course/PROMPTS.md` is filled in. The note also includes the fixes to the agents and skills made during the module.
4. The next module starts with "Start of a module".

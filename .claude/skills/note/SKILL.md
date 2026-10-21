---
name: note
description: Writes a course note to course/NOTES.md about the work done in this session. Use only when the user runs /note or explicitly asks for a course note.
argument-hint: "[module or phase] [extra comment]"
---

# /note: course project note

You record raw material for the course write-up. For every module the assignment asks for: approach, steps, testing, the AI tool and why, 2–3 key prompts. Plus challenges and screenshots. The note must cover exactly these.

**Write the entry in Bulgarian**, in the format below. The write-up is in Bulgarian.

Arguments: `$ARGUMENTS`

## Steps

1. **Module or phase.** Take it from the arguments. If it is missing, infer it from the work done in the session. If it is still unclear, ask and stop.
2. **Prompts of the session.** Read `course/prompts.jsonl`. The last line is the request that started this note: `/note` or a plain-language request, for example from a button. Its `session` is the current session.
   - **Boundary:** in `course/NOTES.md`, find the last entry of this session. Its **Промптове** field says up to which `ts` it got.
   - Take the session's lines with a `ts` after that boundary. If the session has no earlier entry, take all its lines.
   - Do not find the boundary from prompt text. A note started with words or a button does not begin with `/note`.
3. **What was done.** From the session's conversation, take only the work after the session's previous note (if any): the approach and why, the steps (what the AI was asked and what was generated or changed), how it was checked, the problems. Do not repeat what an earlier note already describes. Do not invent: if something did not happen (for example, nothing was tested), say so.
4. **Write.** Add the entry to `course/NOTES.md` under `## <module or phase>`. If that section does not exist, create it before `## Предизвикателства`. Do not touch older entries.
5. **Challenges.** If the session had a real problem (an AI mistake, rework, a tool limitation), also add a line under `## Предизвикателства` with the date and a link to the module.
6. **Show** the user what was added.

## Entry format

```markdown
### <ГГГГ-ММ-ДД> · <кратко заглавие на стъпката>

- **Сесия:** `<session id>` · <модел>
- **Промптове:** `<ts на първия взет ред>` – `<ts на последния ред, т.е. на заявката за бележката>`
- **Подход:** <какво и защо, 1–3 изречения>
- **Стъпки:** <какво е поискано от AI и какво е направено, накратко по точки>
- **Тестване:** <как е проверено; „не е тествано“, ако не е>
- **Инструмент:** <Claude Code и кои възможности: plan mode, субагент, хук, скил…; защо>
- **Ключови промптове (кандидати):**
  - `<час>` <промптът дословно; ако е много дълъг, началото и „…“>
- **Проблеми:** <или „няма“>
- **Скрийншоти:** <път в `course/screenshots/` или „няма“>
- **Коментар:** <допълнителният коментар от аргументите, ако има>
```

## Rules

- The entry is short: raw material, not finished text. The final write-up allows half a page per module.
- 2–4 candidate prompts, the most important ones. No "yes", "continue" or pressed buttons.
- `prompts.jsonl` also logs messages that are not the user's: subagent reports and system notifications. They start with a tag such as `<agent-message` or `<task-notification`. They are never candidate prompts and are not counted as prompts; they only help to see what was called.
- Prompts are quoted verbatim from `prompts.jsonl`, unedited.
- The **Промптове** field is required, with the full `ts` values from `prompts.jsonl`. The next note finds its boundary from it.
- Passwords, hosts and internal names from the prompts never go into the note.

---
name: kiler-ui
description: Kiler's rules for the browser UI in src/ui. Use when writing or changing UI code, UI tests or the Vite build.
---

# Kiler: UI

Decisions come from `docs/PLAN.md` (section 2, M1, M6, M7), `docs/tasks/M1.md` and sections 8–10 of the design (`KILER_DESIGN.md`, local, not in git). Anything not decided yet is listed under "Deferred". Do not invent it; if a task needs such a decision, ask.

## Basics

- React 19, TypeScript, Vite. ESLint and Prettier. TypeScript in strict mode, as a good practice.
- The browser is only the view. Filters, search, permissions and encryption live in C#.
- The build is embedded in `Kiler.exe`. **The app works without internet:** every library is in the build. No CDN and no requests to external addresses.
- UI texts are in Bulgarian, as in the examples in design sections 7–9.

## API

- Requests go only to the same origin, `/api/...`.
- Sign-in uses an HttpOnly cookie set by the server. JavaScript never sees or stores the token, not in `localStorage` or anywhere else.
- The API client handles errors in one place. On 401 it shows a message instead of breaking.
- `EventSource` on `/api/events` is the page's heartbeat. Change notifications come through it later (M6).
- In dev mode Vite proxies `/api` to .NET. The protection is not turned off.

## Secret values

- Passwords (`secret` fields) do not come with lists. The value is requested from the server only on "show" or "copy".
- A shown value does not stay in state longer than needed. It is never logged and never put in a URL.
- Fields above the user's level are shown with a lock.

## HTML, Markdown, Mermaid

- No `dangerouslySetInnerHTML` with unsanitized HTML. All HTML from Markdown goes through DOMPurify.
- Mermaid in strict mode (M7).

## Theme

Light, dark and system (M1.7). CSS variables are the recommendation. Until M6 the choice is kept in `localStorage`; from M6 it is part of the personal configuration through the API.

## Tests

- Vitest for logic:
  - the API client and theme switching (M1.7);
  - bind parameter detection and form generation from templates (M6).
- End-to-end UI behavior is covered by E2E tests with Playwright for .NET.
- Lint and type checking pass with no errors.

## Deferred

| Question | When |
|---|---|
| UI component library | M6 |
| Routing | M6 |
| Data loading and state | M6 |
| Code highlighting in Markdown | M7 |

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
- The app is entirely in English: UI texts, messages and errors (PLAN section 8, rule 8). The Bulgarian texts in the design are only examples of meaning; do not copy or translate them word for word.
- Package versions are the current stable ones when `src/ui` is created (PLAN section 2), checked with `docs-researcher`, not from memory. `package-lock.json` goes into git, because `npm ci` needs it (M1.8, CI in M1.9). `node_modules` and `dist` do not.
- On the developer machine Node.js is a portable install on the user PATH, without admin rights; a shell started before it was put there does not see it. A corporate proxy inspects HTTPS with its own root certificate, which Node 24 trusts from the Windows store, so npm works as it is. Do not turn off TLS checks (`strict-ssl`).

## API

- Requests go only to the same origin, `/api/...`.
- Sign-in uses an HttpOnly cookie set by the server. JavaScript never sees or stores the token, not in `localStorage` or anywhere else.
- The API client handles errors in one place. On 401 it shows a message instead of breaking.
- `EventSource` on `/api/events` is the page's heartbeat. Change notifications come through it later (M6).
- On its first `error` the page closes the `EventSource` and shows that Kiler has stopped. It does not let the browser reconnect: after a stop, each try sends the `kiler` cookie to the old port, and cookies are not separated by port, so by then the cookie can hold the token of a newer Kiler, and the old port can belong to another program (M1.6 security review).
- In dev mode Vite proxies `/api` and the token exchange (`/?t=`) to .NET. The protection is not turned off:
  - the proxy uses `changeOrigin: true`, so `Host` names the .NET port, otherwise the guard returns 400 (M1.4). The browser's `Sec-Fetch-Site: same-origin` passes through;
  - the Vite dev server listens on `127.0.0.1` only, as Kiler does (M1.3), and the page is opened there, not on `localhost` (M1.5);
  - the proxy closes its connection to `/api/events` when the browser closes its own, so a closed tab stops .NET in dev mode too.

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
  - the `EventSource`: closed on the first error, without a reconnect (M1.7);
  - bind parameter detection and form generation from templates (M6).
- End-to-end UI behavior is covered by E2E tests with Playwright for .NET.
- Lint and type checking pass with no errors.

## Deferred

| Question | When |
|---|---|
| Whether all UI texts live in one file | M1.7 or M6 |
| UI component library | M6 |
| Routing | M6 |
| Data loading and state | M6 |
| Code highlighting in Markdown | M7 |

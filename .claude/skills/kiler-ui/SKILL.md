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
- Fixed in M1.7 (`src/ui/package.json`): Vite 8, React 19.3, ESLint 10 with typescript-eslint `strict`, Prettier 3, Vitest 5, jsdom 30.
  - TypeScript stays on `~6.0`: typescript-eslint 8 does not support TypeScript 7, which has no programmatic API. Move to 7 only when typescript-eslint does.
  - create-vite makes React projects with Oxlint by default; Kiler uses ESLint (`--eslint`).
  - jsdom 30 needs Node 24.15 or newer (`engines` in `package.json`).
- On the developer machine Node.js is a portable install on the user PATH, without admin rights; a shell started before it was put there does not see it. A corporate proxy inspects HTTPS with its own root certificate, which Node 24 trusts from the Windows store, so npm works as it is. Do not turn off TLS checks (`strict-ssl`).
- Node 24 runs the TypeScript of `dev/` as it is, by stripping the types: relative imports name the `.ts` file, and only erasable syntax (`erasableSyntaxOnly`), so no parameter properties or enums.
- Prettier writes LF and 2 spaces (`.prettierrc.json`, `.editorconfig`), and `.gitattributes` keeps LF in `src/ui` also where `core.autocrlf` is on (CI on Windows).
- The keys of the `globals` package are camelCase (`globals.sharedWorker`). A wrong key gives `undefined`, which ESLint accepts without a word (M1.7).
- `build.cssTarget` names browsers with `light-dark()` (Chrome and Edge 123, Firefox 120). With an older target, lightningcss rewrites `light-dark()` into variables that resolve once on the root.

## Layout (M1.7)

- `src/texts.ts`: all the UI texts, in one place (decided in M1.7). Components take their texts from it.
- `src/api.ts`: the API client. `src/theme.ts`: the theme.
- `src/events.ts`: the event stream and the hub that shares it; it runs in the page and in the worker, so it uses only what both have. `src/events-worker.ts`: the SharedWorker. `src/connection.ts`: the page side.
- Three TypeScript projects (`tsc -b`): `tsconfig.app.json` (DOM), `tsconfig.worker.json` (WebWorker, only the worker file) and `tsconfig.node.json` (`vite.config.ts` and `dev/`). DOM and WebWorker cannot be in one project. A module imported by the worker is checked in both, so it must not use `SharedWorker` or other names that only one of them has.
- `dev/`: the dev mode (Node), with its tests.
- Commands in `src/ui`: `npm run dev`, `npm run build`, `npm run lint`, `npm run typecheck`, `npm test`, `npm run format:check` (`npm run format` writes).

## API

- Requests go only to the same origin, `/api/...`.
- Sign-in uses an HttpOnly cookie set by the server. JavaScript never sees or stores the token, not in `localStorage` or anywhere else. The cookie is named per instance (M1.7): `kiler`, and `kiler-<key>` with `--data`.
- The API client (`api.ts`) handles errors in one place: every failure becomes an `ApiError` with a message for the user (401, 503, another status, no answer, an answer it cannot read), and the page shows it instead of breaking. An abort by the caller passes through. It refuses a path outside `/api/` before any request.
- `EventSource` on `/api/events` is the heartbeat. Change notifications come through it later (M6).
- **One stream for all the pages** (decided in M1.7). Without TLS the browser talks HTTP/1.1 with at most 6 connections per host, so a stream per page would stall every request from the sixth page on. A SharedWorker (`events-worker.ts`) holds the one `EventSource` and sends its state to each page. The browser ends the worker when the last page of the origin closes, and that closes the stream. A page says `bye` to the worker when it unmounts; a page that closes says nothing, and its port stays in the worker's set.
- Without SharedWorker, or when the worker does not start (its `error` event, or the constructor throws), the page holds a stream of its own. It falls back once: a second `error` must not open a new stream after a stop.
- On its first `error` the stream is closed, in the handler itself, and the state becomes "stopped". It does not let the browser reconnect: after a stop, each try sends the cookie to the old port, and cookies are not separated by port, so by then the cookie can hold the token of a newer Kiler, and the old port can belong to another program (M1.6 security review). The one exception: a page that loads after the stop and joins the worker opens a new stream; that load has just sent the cookie to the same port anyway. The dev mode needs it: the next `npm run dev` serves the page on the same Vite port, and an old tab keeps the old worker alive.
- The SharedWorker is found by its URL and name (`kiler-events`), always with the same options. The constructor must keep the exact form `new SharedWorker(new URL('./events-worker.ts', import.meta.url), {...})`, or Vite does not bundle the worker. The build emits it as `dist/assets/events-worker-<hash>.js` (`worker.format: 'es'`).

## Dev mode (M1.7)

- `npm run dev` (`dev/start.ts`):
  - starts Kiler with `dotnet run --project src/Kiler.App -- --no-browser --data <repo>/dev-data`. `dev-data/` is out of git, and a data folder of its own makes the dev Kiler an instance of its own, apart from the user's Kiler (M1.5);
  - reads Kiler's `Open:` line (`dev/kiler.ts`) and prints Kiler's output with the token hidden;
  - starts the Vite dev server with the proxy to that port (`dev/vite-server.ts`);
  - opens the default browser on `http://127.0.0.1:<vite port>/?t=<token>` through `rundll32 url.dll,FileProtocolHandler`. Not through Vite's `openBrowser`, which first requests the page itself from Node, token and all;
  - stops the dev server when Kiler stops (the last page closed, or Ctrl+C, which reaches Kiler through the shared console).
- `npm run dev -- --no-browser` prints `Open: <dev address with the token>` instead, as Kiler does with `--no-browser`.
- A second `npm run dev` while one runs stops with a message: the dev Kiler is running already, or, after a change of the C# code, `dotnet run` cannot rebuild the exe that the first one runs.
- The proxy (`kilerProxy`) keeps the protection on:
  - only `/api/` (with the slash, so `/apiary` stays with Vite) and the exchange `^/\?t=`, a RegExp on the path with the query. Vite serves `/` without `t`;
  - `changeOrigin: true`, so `Host` names the Kiler port; otherwise the guard returns 400 (M1.4). The browser's cookies, `Sec-Fetch-Site`, `Set-Cookie` and `Location: /` pass as they are;
  - the Vite dev server listens on `127.0.0.1` only, as Kiler does (M1.3), and the page is opened there, not on `localhost` (M1.5);
  - the proxy closes its request to `/api/events` when the browser closes its own, so a closed tab stops Kiler in dev mode too. Vite 8 proxies with http-proxy-3, which does it on the response's `close`; the old `http-proxy` did not. Do not set the proxy's `fetch` options: that path does not close the request. `dev/vite-server.test.ts` checks all of this against a made-up Kiler.
- **No HMR** (decided in M1.7, after the security review): `server.ws: false`, and the plugin `kiler-refuse-websockets` in `vite.config.ts` refuses every WebSocket handshake at once. With HMR, Vite's client tries the port of a dev server that went away every second while the tab is visible, with every cookie of 127.0.0.1 (the user's real Kiler too), and reloads into whatever answers: the reconnect that the event stream avoids. Without the plugin, Vite's proxy leaves the handshake open until the server stops, and the client then tries once more. After a code change, reload the page by hand; a reload keeps Kiler running.
- The dev server sends `Content-Security-Policy: frame-ancestors 'none'` and `X-Frame-Options: DENY`: in dev mode Vite serves the page, without Kiler's guard, so a page on another port of 127.0.0.1 could frame it with the cookie.
- Vite logs the address of a failed proxy request (`http proxy error: /?t=<token>`), so the dev server gets a logger that hides the token (`tokenMaskingLogger`).
- A change of `vite.config.ts` restarts the dev server, which drops the streams; the page then shows "stopped", and Kiler stops after the idle wait. Run `npm run dev` again.

## The embedded build (M1.8)

- A Release build and a publish of Kiler run `npm ci` and `npm run build` and embed `dist/` in `Kiler.exe` (kiler-dotnet, "UI files and publish"). A Debug Kiler has no UI; in dev mode Vite serves it.
- Kiler serves every UI file with a Content Security Policy: `default-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'`. So the UI code:
  - has no inline scripts and no event handler attributes;
  - has no `<style>` elements or `style` attributes in HTML. React's `style` prop is fine, because it goes through the CSSOM;
  - loads nothing from other hosts, and loads fonts and other files as files. Vite puts assets under 4 KB inline as `data:` (`build.assetsInlineLimit`), which the policy allows only for images.

  A library that injects styles or needs more, such as CodeMirror or Mermaid (M6, M7), changes the policy in its own task, with a security review.
- The dev server does not send this policy, because in dev mode Vite and the React plugin use inline scripts. A violation therefore shows only in the embedded build: check the console of the published exe, and the E2E tests (M4) fail on a `securitypolicyviolation` event.
- Addresses of the UI's own routing (M6) have no dot in their last segment. Kiler gives `index.html` only to such addresses; a name with a dot is a file, and a missing one is 404 (also on a reload).
- `/assets/` is cached for a year without a check, because Vite puts there only names with a hash of the content. Files in `public/` keep their names, are served from the root and are checked each time; nothing goes into `public/assets/`.

## Secret values

- Passwords (`secret` fields) do not come with lists. The value is requested from the server only on "show" or "copy".
- A shown value does not stay in state longer than needed. It is never logged and never put in a URL.
- Fields above the user's level are shown with a lock.

## HTML, Markdown, Mermaid

- No `dangerouslySetInnerHTML` with unsanitized HTML. All HTML from Markdown goes through DOMPurify.
- Mermaid in strict mode (M7).

## Theme

Light, dark and system (M1.7). Until M6 the choice is kept in `localStorage` (`kiler.theme`); from M6 it is part of the personal configuration through the API.

- Every colour is a CSS variable with `light-dark(<light>, <dark>)` (`index.css`). The root has `color-scheme: light dark`, so "system" follows Windows by CSS alone, also before the script runs and when Windows changes its theme. A light or dark choice sets `data-theme` on the root, which sets `color-scheme` to one side.
- `main.tsx` applies the saved choice before the first render. A storage that throws counts as no choice.
- Components with themes of their own (CodeMirror, Mermaid in M6/M7) need the resolved theme: `data-theme`, or `matchMedia('(prefers-color-scheme: dark)')` without it.

## Tests

- Vitest for logic:
  - the API client and theme switching (M1.7);
  - the `EventSource`: closed on the first error, without a reconnect, and the worker hub (M1.7);
  - the dev server against a made-up Kiler on 127.0.0.1: `Host`, the exchange, the close of the stream, a foreign `Host`, the framing headers, no WebSocket, the token hidden in the log (M1.7);
  - bind parameter detection and form generation from templates (M6).
- The default environment is `node`. A file that needs the DOM starts with `// @vitest-environment jsdom`.
- Browser objects that a test drives (EventSource, MessagePort, SharedWorker) are fakes in `src/test-fakes.ts` or in the test; globals are replaced with `vi.stubGlobal` and restored with `vi.unstubAllGlobals()`.
- A new test is checked to fail against broken code, as in `kiler-dotnet`.
- End-to-end UI behavior is covered by E2E tests with Playwright for .NET.
- Lint and type checking pass with no errors.

## Manual checks

- The Claude Code in-app browser blocks the network requests of a SharedWorker: its module imports and its `EventSource` fail, while a dedicated Worker and the page fetch normally (checked in M1.7). There the dev mode falls back to a stream per page, because the worker script does not load, and the build shows "Stopped", because the worker's stream fails. Check the shared stream in Chrome or Edge.
- Run `npm run dev -- --no-browser` from Claude, so it opens no window on the user's desktop; the address with the token is then in its output.
- In the user's PowerShell it is `npm.cmd run dev`: the AllSigned policy stops the unsigned `npm.ps1`. In cmd it is `npm`.
- In Chrome or Edge, `chrome://inspect/#workers` or `edge://inspect/#workers` lists the shared worker `kiler-events`, and `Get-NetTCPConnection -RemotePort <kiler port> -State Established` counts the streams (one for all the tabs).
- The embedded build: `publish.ps1`, then `artifacts\publish\Kiler.exe --no-browser --data <temporary folder>` and the `Open:` address in Chrome or Edge. Its console shows CSP violations, which the dev mode does not have.

## Deferred

| Question | When |
|---|---|
| UI component library | M6 |
| Routing (addresses without a dot in the last segment, M1.8) | M6 |
| Data loading and state | M6 |
| Code highlighting in Markdown | M7 |

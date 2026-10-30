import { useEffect, useState } from 'react';
import { BrowserRouter, Link, Navigate, Route, Routes } from 'react-router';
import { lockSession, type Session } from './api.ts';
import { connectEvents } from './connection.ts';
import { DataGate } from './DataGate.tsx';
import type { ConnectionState } from './events.ts';
import { Home } from './Home.tsx';
import { SessionContext, SessionStore, useSessionSnapshot, useSessionStore, type SessionView } from './session.ts';
import { SignIn } from './SignIn.tsx';
import { texts } from './texts.ts';
import { applyThemeChoice, readThemeChoice, saveThemeChoice, themeChoices, type ThemeChoice } from './theme.ts';

/** The page: the session of the event stream (session.ts), and the frame with the routes. */
export function App() {
  const [store] = useState(() => new SessionStore());
  useEffect(() => {
    const leave = connectEvents(
      (state) => store.connectionChanged(state),
      () => store.sessionChanged(),
    );
    return () => {
      leave();
      store.stop();
    };
  }, [store]);

  return (
    <SessionContext value={store}>
      <BrowserRouter>
        <Frame />
      </BrowserRouter>
    </SessionContext>
  );
}

/**
 * The frame of every page: the header with the connection, the account and the theme, and the content below it. The
 * content is there only while the stream is connected: a stop drops everything the page shows.
 */
function Frame() {
  const { connection, view } = useSessionSnapshot();
  const [lockFailed, setLockFailed] = useState(false);

  return (
    <div className="frame">
      <header className="frame-header">
        <span className="brand">{texts.appName}</span>
        <ConnectionBadge state={connection} />
        <ThemeSwitch />
        <Account view={view} onLockFailed={setLockFailed} />
      </header>
      {connection === 'stopped' && (
        <div className="banner banner-danger" role="alert">
          {texts.connection.stoppedMessage}
        </div>
      )}
      {lockFailed && (
        <div className="banner banner-danger" role="alert">
          {texts.account.lockFailed}
        </div>
      )}
      <main className="frame-main">
        {connection === 'connected' && (
          <DataGate>
            <SessionGate view={view} />
          </DataGate>
        )}
        {connection === 'connecting' && <p className="muted">{texts.connection.connecting}</p>}
      </main>
    </div>
  );
}

function ConnectionBadge({ state }: { state: ConnectionState }) {
  return (
    <span className={`badge badge-${state}`} role="status">
      {texts.connection[state]}
    </span>
  );
}

/** The account of the session, with "Lock" for a signed-in user and "Sign in" for a guest. */
function Account({ view, onLockFailed }: { view: SessionView; onLockFailed: (failed: boolean) => void }) {
  const store = useSessionStore();
  const [locking, setLocking] = useState(false);
  if (view.state !== 'ready') {
    return null;
  }

  const session = view.session;
  const lock = () => {
    setLocking(true);
    onLockFailed(false);
    lockSession().then(
      (locked) => {
        setLocking(false);
        store.set(locked);
      },
      () => {
        setLocking(false);
        onLockFailed(true);
        store.reload();
      },
    );
  };

  if (session.signedIn) {
    return (
      <span className="account">
        <span title={session.account ?? undefined}>{session.displayName}</span>
        <button type="button" onClick={lock} disabled={locking}>
          {texts.account.lock}
        </button>
      </span>
    );
  }

  return session.guestMode ? (
    <span className="account">
      <span>{texts.account.guest}</span>
      <Link className="button" to="/sign-in">
        {texts.account.signIn}
      </Link>
    </span>
  ) : null;
}

function ThemeSwitch() {
  const [themeChoice, setThemeChoice] = useState(() => readThemeChoice());
  const changeTheme = (choice: ThemeChoice) => {
    applyThemeChoice(choice);
    saveThemeChoice(choice);
    setThemeChoice(choice);
  };

  return (
    <label className="theme-switch">
      {texts.theme.label}
      <select value={themeChoice} onChange={(event) => changeTheme(event.target.value as ThemeChoice)}>
        {themeChoices.map((choice) => (
          <option key={choice} value={choice}>
            {texts.theme[choice]}
          </option>
        ))}
      </select>
    </label>
  );
}

/** The routes once the session is read. While it is read again, after a `session` event, nothing of the last one shows. */
function SessionGate({ view }: { view: SessionView }) {
  const store = useSessionStore();
  switch (view.state) {
    case 'waiting':
    case 'reading':
      return <p className="muted">{texts.session.reading}</p>;
    case 'failed':
      return (
        <section className="card stack">
          <p className="banner banner-danger" role="alert">
            {view.message}
          </p>
          <div>
            <button type="button" onClick={() => store.reload()}>
              {texts.session.tryAgain}
            </button>
          </div>
        </section>
      );
    case 'ready':
      return <AppRoutes session={view.session} />;
  }
}

function AppRoutes({ session }: { session: Session }) {
  return (
    <Routes>
      <Route index element={<Home session={session} />} />
      <Route path="sign-in" element={<SignIn session={session} />} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}

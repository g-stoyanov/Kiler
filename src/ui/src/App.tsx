import { Fragment, useEffect, useState, type ReactNode } from 'react';
import { BrowserRouter, Link, Navigate, Outlet, Route, Routes, useLocation, useParams } from 'react-router';
import { getHealth, lockSession, type Session } from './api.ts';
import { Browse } from './Browse.tsx';
import { ChangePassword } from './ChangePassword.tsx';
import { connectEvents } from './connection.ts';
import { DataGate } from './DataGate.tsx';
import type { ConnectionState } from './events.ts';
import { recordPath } from './fields.ts';
import { History } from './History.tsx';
import { Levels } from './Levels.tsx';
import { RecordForm } from './RecordForm.tsx';
import { RecordPage } from './RecordPage.tsx';
import { SessionContext, SessionStore, useSessionSnapshot, useSessionStore, type SessionView } from './session.ts';
import { SignIn } from './SignIn.tsx';
import { signInFrom } from './signIn.ts';
import { texts } from './texts.ts';
import { applyThemeChoice, readThemeChoice, saveThemeChoice, themeChoices, type ThemeChoice } from './theme.ts';
import { Trash } from './Trash.tsx';
import { Users } from './Users.tsx';

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
 * The frame of every page: the header with the connection, the links, the account and the theme, the content below it,
 * and the foot line with the data folder and the version. The content is there only while the stream is connected: a
 * stop drops everything the page shows. The foot line keeps what it last read, so the data folder stays in sight.
 */
function Frame() {
  const { connection, view } = useSessionSnapshot();
  const [lockFailed, setLockFailed] = useState(false);
  const [folder, setFolder] = useState<string | null>(null);
  const version = useVersion(connection === 'connected');

  return (
    <div className="frame">
      <header className="frame-header">
        <Link className="brand" to="/">
          {texts.appName}
        </Link>
        <ConnectionBadge state={connection} />
        <HeaderLinks view={view} />
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
          <DataGate onFolder={setFolder}>
            <SessionGate view={view} />
          </DataGate>
        )}
        {connection === 'connecting' && <p className="muted">{texts.connection.connecting}</p>}
      </main>
      <footer className="frame-foot">
        {folder !== null && <span>{texts.data.folder(folder)}</span>}
        {version !== undefined && <span>{texts.frame.version(version)}</span>}
      </footer>
    </div>
  );
}

/** The version of Kiler, read once the stream has connected; undefined until then, and when the read fails. */
function useVersion(connected: boolean): string | undefined {
  const [version, setVersion] = useState<string>();
  useEffect(() => {
    if (!connected) {
      return;
    }

    const abort = new AbortController();
    getHealth(abort.signal).then(
      (health) => setVersion(health.version),
      () => undefined,
    );
    return () => abort.abort();
  }, [connected]);
  return version;
}

function ConnectionBadge({ state }: { state: ConnectionState }) {
  return (
    <span className={`badge badge-${state}`} role="status">
      {texts.connection[state]}
    </span>
  );
}

/**
 * The account of the session: for a signed-in user "Password", the voluntary change, and "Lock"; for a guest "Sign in".
 * A session with a temporary password sees only its change, so it gets no "Password".
 */
function Account({ view, onLockFailed }: { view: SessionView; onLockFailed: (failed: boolean) => void }) {
  const store = useSessionStore();
  const location = useLocation();
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
        {!session.mustChangePassword && (
          <Link className="button" to="/password">
            {texts.account.password}
          </Link>
        )}
        <button type="button" onClick={lock} disabled={locking}>
          {texts.account.lock}
        </button>
      </span>
    );
  }

  return session.guestMode ? (
    <span className="account">
      <span>{texts.account.guest}</span>
      <Link className="button" to="/sign-in" state={signInFrom(location)}>
        {texts.account.signIn}
      </Link>
    </span>
  ) : null;
}

/** The trash for a signed-in user, and the admin screens for an admin, once the temporary password is changed. */
function HeaderLinks({ view }: { view: SessionView }) {
  if (view.state !== 'ready' || !view.session.signedIn || view.session.mustChangePassword) {
    return null;
  }

  return (
    <nav className="header-links">
      <Link to="/trash">{texts.frame.trash}</Link>
      {view.session.isAdmin && (
        <>
          <Link to="/users">{texts.admin.users}</Link>
          <Link to="/levels">{texts.admin.levels}</Link>
        </>
      )}
    </nav>
  );
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
      // Mounted again when the session signs in or out or its levels change, so no screen keeps what it read with other
      // levels: in guest mode "Lock" gives the guest's session through `store.set`, before the `session` event (M4.8).
      return (
        <AppRoutes
          key={`${view.session.signedIn}:${view.session.levels.join(',')}`}
          session={view.session}
          locked={view.locked}
        />
      );
  }
}

/**
 * The routes that the session may see (M4.8): without a session and without guest mode only the sign-in, which every
 * other address leads to and goes back to; with a temporary password only its change; else the screens, where a
 * screen that needs a session leads a guest to the sign-in the same way. The server checks every request anyway.
 *
 * The routes alone send a page to the sign-in after a lock: a screen that also navigates races them, as the store
 * renders the new session at once and React Router changes the address in a transition (M4.8 code review).
 */
function AppRoutes({ session, locked }: { session: Session; locked: boolean }) {
  if (!session.signedIn && !session.guestMode) {
    return (
      <Routes>
        <Route path="sign-in" element={<SignIn session={session} locked={locked} />} />
        <Route path="*" element={<ToSignIn />} />
      </Routes>
    );
  }

  if (session.signedIn && session.mustChangePassword) {
    return (
      <Routes>
        <Route path="password" element={<ChangePassword session={session} />} />
        <Route path="*" element={<Navigate to="/password" replace />} />
      </Routes>
    );
  }

  return (
    <Routes>
      <Route path="sign-in" element={<SignIn session={session} locked={locked} />} />
      <Route path="password" element={session.signedIn ? <ChangePassword session={session} /> : <ToSignIn />} />
      <Route element={<Screens locked={locked} />}>
        <Route index element={<Browse session={session} />} />
        <Route
          path="new"
          element={
            <EditorOnly session={session} back="/">
              <RecordForm session={session} />
            </EditorOnly>
          }
        />
        <Route path="records/:id" element={<ByRecord render={(id) => <RecordPage id={id} session={session} />} />} />
        <Route
          path="records/:id/edit"
          element={
            <ByRecord
              render={(id) => (
                <EditorOnly session={session} back={recordPath(id)}>
                  <RecordForm id={id} session={session} />
                </EditorOnly>
              )}
            />
          }
        />
        <Route
          path="records/:id/history"
          element={<ByRecord render={(id) => <History id={id} session={session} />} />}
        />
        <Route path="trash" element={<Trash session={session} />} />
        <Route
          path="users"
          element={
            <AdminOnly session={session}>
              <Users session={session} />
            </AdminOnly>
          }
        />
        <Route
          path="levels"
          element={
            <AdminOnly session={session}>
              <Levels />
            </AdminOnly>
          }
        />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}

/** The screens, under the banner of a guest after a lock (M4.8): the page shows only level 0 again. */
function Screens({ locked }: { locked: boolean }) {
  return (
    <>
      {locked && (
        <p className="banner banner-warning" role="status">
          {texts.frame.locked}
        </p>
      )}
      <Outlet />
    </>
  );
}

/**
 * A screen of one record, mounted again for each record, so nothing of one record stays on the screen of another, such
 * as a shown `secret` after the link of a `ref`.
 */
function ByRecord({ render }: { render: (id: string) => ReactNode }) {
  const { id } = useParams();
  return id === undefined ? <Navigate to="/" replace /> : <Fragment key={id}>{render(id)}</Fragment>;
}

/**
 * A screen that writes records: a guest goes to the sign-in, and a user without `canEdit` to `back`, as the form has
 * nothing for them before the private records (decided at the start of M6). The server checks every request anyway.
 */
function EditorOnly({ session, back, children }: { session: Session; back: string; children: ReactNode }) {
  if (!session.signedIn) {
    return <ToSignIn />;
  }

  return session.canEdit ? <>{children}</> : <Navigate to={back} replace />;
}

/**
 * An admin screen (M4.9): a guest goes to the sign-in, as on `/password`, and a user who is not an admin to the start
 * page. The server checks every request anyway, also that the admin holds every level.
 */
function AdminOnly({ session, children }: { session: Session; children: ReactNode }) {
  if (!session.signedIn) {
    return <ToSignIn />;
  }

  return session.isAdmin ? <>{children}</> : <Navigate to="/" replace />;
}

/** To the sign-in, which goes back to this address once it has a session, as after a lock. */
function ToSignIn() {
  const location = useLocation();
  return <Navigate to="/sign-in" replace state={signInFrom(location)} />;
}

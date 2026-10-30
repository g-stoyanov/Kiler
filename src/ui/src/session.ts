import { createContext, useContext, useSyncExternalStore } from 'react';
import { getSession, messageOf, type Session } from './api.ts';
import type { ConnectionState } from './events.ts';

/**
 * What the page knows of the session: `waiting` until the stream connects and after it stops, `reading` while
 * `GET /api/session` runs, then the session or why it could not be read. `locked` says that this page has seen a
 * session end (the automatic lock, "Lock", a lock on another page), so its sign-in is the lock screen; it is false once
 * a session starts again, and for a page that has seen no session since its stream connected.
 */
export type SessionView =
  | { state: 'waiting' }
  | { state: 'reading' }
  | { state: 'ready'; session: Session; locked: boolean }
  | { state: 'failed'; message: string };

export interface SessionSnapshot {
  connection: ConnectionState;
  view: SessionView;
}

const initial: SessionSnapshot = { connection: 'connecting', view: { state: 'waiting' } };

/**
 * The session in the page (M4.4, M4.5b), with the state of the event stream (connection.ts). It reads the session once
 * the stream has connected, which behind the SharedWorker is once the page's port is in the worker's set, so a lock
 * after it reaches the page as a `session` event; and again on each connect and each `session` event. A lock while no
 * stream held the page gives no event, so the read at the connect is what finds it. On the event and on a stop it drops
 * the session first, so the page drops everything it shows. A session without a sign-in after one with it is `locked`
 * (M4.8), so the page shows the lock screen.
 *
 * `GET /api/session` is activity for the automatic lock, so it reads it only on a connect, an event or an action of the
 * user, never on a timer. For `useSyncExternalStore`: each change is a new snapshot.
 */
export class SessionStore {
  readonly #read: (signal: AbortSignal) => Promise<Session>;
  readonly #listeners = new Set<() => void>();
  #snapshot = initial;
  #reading: AbortController | undefined;
  // Whether a session has been seen since the stream connected, for `locked`.
  #hadSession = false;

  constructor(read: (signal: AbortSignal) => Promise<Session> = getSession) {
    this.#read = read;
  }

  readonly getSnapshot = (): SessionSnapshot => this.#snapshot;

  readonly subscribe = (listener: () => void): (() => void) => {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  };

  /** The state of the stream: a connect reads the session, a stop drops it. */
  connectionChanged(connection: ConnectionState): void {
    if (connection === this.#snapshot.connection) {
      return;
    }

    if (connection === 'connected') {
      this.#readAgain(connection);
    } else {
      this.#cancel();
      this.#hadSession = false;
      this.#set({ connection, view: { state: 'waiting' } });
    }
  }

  /** The `session` event: a sign-in or a lock, on this page or another. */
  sessionChanged(): void {
    this.reload();
  }

  /** Reads the session again, for an action of the user, such as "Try again" or a refusal of no right. */
  reload(): void {
    if (this.#snapshot.connection === 'connected') {
      this.#readAgain('connected');
    }
  }

  /** The session from the answer of an action (sign-in, "Lock", change of password). */
  set(session: Session): void {
    if (this.#snapshot.connection === 'connected') {
      this.#cancel();
      this.#set({ connection: 'connected', view: this.#ready(session) });
    }
  }

  /** For a page that goes away, or the effect that StrictMode runs twice: back to the start. */
  stop(): void {
    this.#cancel();
    this.#hadSession = false;
    this.#set(initial);
  }

  #readAgain(connection: ConnectionState): void {
    this.#cancel();
    const reading = new AbortController();
    this.#reading = reading;
    this.#set({ connection, view: { state: 'reading' } });
    this.#read(reading.signal).then(
      (session) => this.#finish(reading, () => this.#ready(session)),
      (error: unknown) => this.#finish(reading, () => ({ state: 'failed', message: messageOf(error) })),
    );
  }

  #finish(reading: AbortController, view: () => SessionView): void {
    // A read that a later one replaced, or that a stop cancelled, gives nothing.
    if (this.#reading === reading) {
      this.#reading = undefined;
      this.#set({ connection: this.#snapshot.connection, view: view() });
    }
  }

  #ready(session: Session): SessionView {
    const locked = !session.signedIn && this.#hadSession;
    this.#hadSession ||= session.signedIn;
    return { state: 'ready', session, locked };
  }

  #cancel(): void {
    this.#reading?.abort();
    this.#reading = undefined;
  }

  #set(snapshot: SessionSnapshot): void {
    this.#snapshot = snapshot;
    for (const listener of this.#listeners) {
      listener();
    }
  }
}

export const SessionContext = createContext<SessionStore | null>(null);

/** The store of the page, which `App` provides. */
export function useSessionStore(): SessionStore {
  const store = useContext(SessionContext);
  if (store === null) {
    throw new Error('No SessionContext above this component.');
  }

  return store;
}

/** The session and the stream as they are now; the component renders again on each change. */
export function useSessionSnapshot(): SessionSnapshot {
  const store = useSessionStore();
  return useSyncExternalStore(store.subscribe, store.getSnapshot);
}

/**
 * The heartbeat of the pages: the event stream `/api/events`. Kiler stops 10 s after the last stream closes (M1.6).
 *
 * All the pages of one Kiler share one stream, held by a SharedWorker (decided in M1.7). Without TLS the browser
 * talks HTTP/1.1 to Kiler, with at most 6 connections per host, so a stream per page would stall every request from
 * the sixth page on. The browser ends the worker when the last page of its origin closes, which closes the stream.
 * A browser without SharedWorker gets a stream per page.
 *
 * This module runs both in the worker (events-worker.ts) and in the page; connection.ts is the page side.
 */

export type ConnectionState = 'connecting' | 'connected' | 'stopped';

/**
 * A message from the worker to a page: the state of the stream, or its `session` event, on which the page drops what it
 * shows and reads the session again (M4.5b). Change notifications (M6) will be another type.
 */
export type HubMessage = { type: 'state'; state: ConnectionState } | { type: 'session' };

/** A message from a page to the worker: the page lets its port go. */
export type PageMessage = { type: 'bye' };

export const eventsPath = '/api/events';

export type StateListener = (state: ConnectionState) => void;

/** Called on each `session` event: a sign-in or a lock (M4.5b). The event has no data. */
export type SessionListener = () => void;

/**
 * One `EventSource` on {@link eventsPath}. On its first error it is closed for good, before the browser can
 * reconnect: after a stop, each try would send the cookie to the old port, which by then can belong to another
 * program, and the cookie can hold the token of a newer Kiler (M1.6 security review).
 */
export class KilerEvents {
  readonly #createSource: (url: string) => EventSource;
  readonly #listeners = new Set<StateListener>();
  readonly #sessionListeners = new Set<SessionListener>();
  #source: EventSource | undefined;
  #state: ConnectionState = 'connecting';

  constructor(createSource: (url: string) => EventSource = (url) => new EventSource(url)) {
    this.#createSource = createSource;
  }

  get state(): ConnectionState {
    return this.#state;
  }

  /**
   * Opens the stream, unless it is open. After a stop it opens a new one: only a page that has just loaded from
   * Kiler asks for that, and that load has already sent the cookie to the same port.
   */
  start(): void {
    if (this.#source !== undefined) {
      return;
    }

    const source = this.#createSource(eventsPath);
    this.#source = source;
    this.#setState('connecting');
    const connected = () => {
      if (this.#source === source) {
        this.#setState('connected');
      }
    };
    source.addEventListener('open', connected);
    source.addEventListener('ping', connected);
    source.addEventListener('session', () => {
      if (this.#source === source) {
        for (const listener of this.#sessionListeners) {
          listener();
        }
      }
    });
    source.addEventListener('error', () => {
      // Closed in the error handler itself, so the browser does not reconnect.
      source.close();
      if (this.#source === source) {
        this.#source = undefined;
        this.#setState('stopped');
      }
    });
  }

  /** Closes the stream without calling it a stop, for a page that goes away. */
  close(): void {
    this.#source?.close();
    this.#source = undefined;
  }

  /** Calls the listener with the state now and on every change. Returns the function that stops the calls. */
  subscribe(listener: StateListener): () => void {
    this.#listeners.add(listener);
    listener(this.#state);
    return () => this.#listeners.delete(listener);
  }

  /** Calls the listener on each `session` event. Returns the function that stops the calls. */
  onSession(listener: SessionListener): () => void {
    this.#sessionListeners.add(listener);
    return () => this.#sessionListeners.delete(listener);
  }

  #setState(state: ConnectionState): void {
    if (state === this.#state) {
      return;
    }

    this.#state = state;
    for (const listener of this.#listeners) {
      listener(state);
    }
  }
}

/** The part of a `MessagePort` that the hub uses. */
export interface PagePort {
  postMessage(message: HubMessage): void;
  addEventListener(type: 'message', listener: (event: MessageEvent<PageMessage>) => void): void;
  start(): void;
}

/** The worker side: one stream for every page that connects, and its state and events sent to each of them. */
export function createEventsHub(events: KilerEvents): { connect(port: PagePort): void } {
  const ports = new Set<PagePort>();
  const send = (message: HubMessage) => {
    for (const port of ports) {
      port.postMessage(message);
    }
  };
  events.subscribe((state) => send({ type: 'state', state }));
  events.onSession(() => send({ type: 'session' }));

  return {
    connect(port) {
      // A page that closes says nothing, so its port stays in the set; a message to it goes nowhere.
      port.addEventListener('message', (event) => {
        if (event.data.type === 'bye') {
          ports.delete(port);
        }
      });
      port.start();
      events.start();
      ports.add(port);
      port.postMessage({ type: 'state', state: events.state });
    },
  };
}

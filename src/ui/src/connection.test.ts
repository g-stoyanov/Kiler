import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { connectEvents } from './connection.ts';
import { eventsPath, type ConnectionState, type HubMessage, type PageMessage } from './events.ts';
import { FakeEventSource, FakePort, lastSource } from './test-fakes.ts';

beforeEach(() => {
  FakeEventSource.created = [];
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('connectEvents', () => {
  /** The SharedWorker that the page makes, as the test sees it. */
  class FakeSharedWorker {
    static created: FakeSharedWorker[] = [];
    readonly url: URL;
    readonly options: WorkerOptions | undefined;
    readonly port = new FakePort();
    readonly #errorListeners: (() => void)[] = [];

    constructor(url: URL, options?: WorkerOptions) {
      this.url = url;
      this.options = options;
      this.port.postMessage = (message: unknown) => {
        this.port.sent.push(message as PageMessage);
      };
      FakeSharedWorker.created.push(this);
    }

    addEventListener(_type: 'error', listener: () => void): void {
      this.#errorListeners.push(listener);
    }

    fail(): void {
      for (const listener of this.#errorListeners) {
        listener();
      }
    }
  }

  function worker(): FakeSharedWorker {
    const created = FakeSharedWorker.created.at(-1);
    if (created === undefined) {
      throw new Error('No SharedWorker was created.');
    }

    return created;
  }

  beforeEach(() => {
    FakeSharedWorker.created = [];
    vi.stubGlobal('EventSource', FakeEventSource);
  });

  it('joins the shared worker of the events and takes the state from it', () => {
    vi.stubGlobal('SharedWorker', FakeSharedWorker);
    const states: ConnectionState[] = [];

    connectEvents(
      (state) => states.push(state),
      () => {},
      () => {},
    );
    worker().port.deliver({ type: 'state', state: 'connected' } satisfies HubMessage);

    expect(worker().url.pathname).toMatch(/\/events-worker\.ts$/);
    expect(worker().options).toEqual({ type: 'module', name: 'kiler-events' });
    expect(worker().port.started).toBe(true);
    expect(FakeEventSource.created).toHaveLength(0);
    expect(states).toEqual(['connected']);
  });

  it('passes the "session" event of the worker on', () => {
    vi.stubGlobal('SharedWorker', FakeSharedWorker);
    let sessions = 0;

    connectEvents(
      () => {},
      () => sessions++,
      () => {},
    );
    worker().port.deliver({ type: 'state', state: 'connected' } satisfies HubMessage);
    worker().port.deliver({ type: 'session' } satisfies HubMessage);

    expect(sessions).toBe(1);
  });

  it('passes the "changed" event of the worker on', () => {
    vi.stubGlobal('SharedWorker', FakeSharedWorker);
    let changes = 0;
    let sessions = 0;

    connectEvents(
      () => {},
      () => sessions++,
      () => changes++,
    );
    worker().port.deliver({ type: 'state', state: 'connected' } satisfies HubMessage);
    worker().port.deliver({ type: 'changed' } satisfies HubMessage);
    worker().port.deliver({ type: 'changed' } satisfies HubMessage);

    expect(changes).toBe(2);
    expect(sessions).toBe(0);
  });

  it('says bye to the worker and closes its port when the page leaves', () => {
    vi.stubGlobal('SharedWorker', FakeSharedWorker);

    const leave = connectEvents(
      () => {},
      () => {},
      () => {},
    );
    leave();

    expect(worker().port.sent).toEqual([{ type: 'bye' }]);
    expect(worker().port.closed).toBe(true);
  });

  it('holds a stream of its own when the worker does not start', () => {
    vi.stubGlobal('SharedWorker', FakeSharedWorker);
    const states: ConnectionState[] = [];

    const leave = connectEvents(
      (state) => states.push(state),
      () => {},
      () => {},
    );
    worker().fail();
    lastSource().emit('open');
    leave();

    expect(FakeEventSource.created).toHaveLength(1);
    expect(states).toEqual(['connecting', 'connected']);
    expect(lastSource().closed).toBe(true);
  });

  it('falls back once, so a second error of the worker opens no new stream after a stop', () => {
    vi.stubGlobal('SharedWorker', FakeSharedWorker);
    const states: ConnectionState[] = [];

    connectEvents(
      (state) => states.push(state),
      () => {},
      () => {},
    );
    worker().fail();
    lastSource().emit('error');
    worker().fail();

    expect(FakeEventSource.created).toHaveLength(1);
    expect(states).toEqual(['connecting', 'stopped']);
  });

  it('joins the worker again after a leave, as StrictMode runs the effect twice', () => {
    vi.stubGlobal('SharedWorker', FakeSharedWorker);
    const states: ConnectionState[] = [];

    connectEvents(
      (state) => states.push(state),
      () => {},
      () => {},
    )();
    connectEvents(
      (state) => states.push(state),
      () => {},
      () => {},
    );
    worker().port.deliver({ type: 'state', state: 'connected' } satisfies HubMessage);

    expect(FakeSharedWorker.created).toHaveLength(2);
    expect(FakeSharedWorker.created[0]?.port.closed).toBe(true);
    expect(worker().port.closed).toBe(false);
    expect(FakeEventSource.created).toHaveLength(0);
    expect(states).toEqual(['connected']);
  });

  it('opens nothing when the worker fails after the page has left', () => {
    vi.stubGlobal('SharedWorker', FakeSharedWorker);

    const leave = connectEvents(
      () => {},
      () => {},
      () => {},
    );
    leave();
    worker().fail();

    expect(FakeEventSource.created).toHaveLength(0);
  });

  it('holds a stream of its own without SharedWorker', () => {
    vi.stubGlobal('SharedWorker', undefined);
    const states: ConnectionState[] = [];

    const leave = connectEvents(
      (state) => states.push(state),
      () => {},
      () => {},
    );
    lastSource().emit('error');
    leave();

    expect(lastSource().url).toBe(eventsPath);
    expect(states).toEqual(['connecting', 'stopped']);
  });

  it('passes the "session" event of a stream of its own on, until the page leaves', () => {
    vi.stubGlobal('SharedWorker', undefined);
    let sessions = 0;

    const leave = connectEvents(
      () => {},
      () => sessions++,
      () => {},
    );
    lastSource().emit('ping');
    lastSource().emit('session');
    leave();
    lastSource().emit('session');

    expect(sessions).toBe(1);
  });

  it('passes the "changed" event of a stream of its own on, until the page leaves', () => {
    vi.stubGlobal('SharedWorker', undefined);
    let changes = 0;

    const leave = connectEvents(
      () => {},
      () => {},
      () => changes++,
    );
    lastSource().emit('ping');
    lastSource().emit('changed');
    leave();
    lastSource().emit('changed');

    expect(changes).toBe(1);
  });

  it('holds a stream of its own when the browser refuses the worker', () => {
    vi.stubGlobal('SharedWorker', function refused() {
      throw new DOMException('Refused', 'SecurityError');
    });

    connectEvents(
      () => {},
      () => {},
      () => {},
    );

    expect(FakeEventSource.created).toHaveLength(1);
  });
});

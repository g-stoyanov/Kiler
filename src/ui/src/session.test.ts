import { describe, expect, it } from 'vitest';
import { ApiError, type Session } from './api.ts';
import { SessionStore, type SessionSnapshot } from './session.ts';

const guest: Session = {
  account: 'demo.user',
  signedIn: false,
  displayName: null,
  levels: [0],
  canEdit: false,
  isAdmin: false,
  mustChangePassword: false,
  guestMode: true,
};

const admin: Session = {
  ...guest,
  signedIn: true,
  displayName: 'Demo Admin',
  levels: [0, 1, 2, 3],
  canEdit: true,
  isAdmin: true,
};

/** A read of the session that the test answers. */
interface Read {
  signal: AbortSignal;
  answer(session: Session): Promise<void>;
  fail(error: unknown): Promise<void>;
}

function newStore(): { store: SessionStore; reads: Read[]; snapshots: SessionSnapshot[] } {
  const reads: Read[] = [];
  const store = new SessionStore(
    (signal) =>
      new Promise<Session>((resolve, reject) => {
        reads.push({
          signal,
          // Resolved, then a turn of the queue, so the store has taken the answer.
          answer: (session) => {
            resolve(session);
            return settle();
          },
          fail: (error) => {
            reject(error);
            return settle();
          },
        });
      }),
  );
  const snapshots: SessionSnapshot[] = [];
  store.subscribe(() => snapshots.push(store.getSnapshot()));
  return { store, reads, snapshots };
}

function settle(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

function lastRead(reads: Read[]): Read {
  const read = reads.at(-1);
  if (read === undefined) {
    throw new Error('The session was not read.');
  }

  return read;
}

describe('SessionStore', () => {
  it('reads nothing until the stream has connected', () => {
    const { store, reads } = newStore();

    store.connectionChanged('connecting');

    expect(reads).toHaveLength(0);
    expect(store.getSnapshot()).toEqual({ connection: 'connecting', view: { state: 'waiting' } });
  });

  it('reads the session once the stream has connected', async () => {
    const { store, reads } = newStore();

    store.connectionChanged('connected');
    expect(store.getSnapshot().view).toEqual({ state: 'reading' });
    await lastRead(reads).answer(admin);

    expect(reads).toHaveLength(1);
    expect(store.getSnapshot()).toEqual({ connection: 'connected', view: { state: 'ready', session: admin } });
  });

  it('drops the session on the "session" event, and shows the one it reads then', async () => {
    const { store, reads, snapshots } = newStore();
    store.connectionChanged('connected');
    await lastRead(reads).answer(admin);

    store.sessionChanged();
    expect(store.getSnapshot().view).toEqual({ state: 'reading' });
    await lastRead(reads).answer(guest);

    expect(reads).toHaveLength(2);
    expect(snapshots.map((snapshot) => snapshot.view.state)).toEqual(['reading', 'ready', 'reading', 'ready']);
    expect(store.getSnapshot().view).toEqual({ state: 'ready', session: guest });
  });

  it('drops the session when the stream stops, and reads nothing then', async () => {
    const { store, reads } = newStore();
    store.connectionChanged('connected');
    await lastRead(reads).answer(admin);

    store.connectionChanged('stopped');
    store.sessionChanged();
    store.reload();
    store.set(admin);

    expect(reads).toHaveLength(1);
    expect(store.getSnapshot()).toEqual({ connection: 'stopped', view: { state: 'waiting' } });
  });

  it('reads the session again when the stream connects again', async () => {
    const { store, reads } = newStore();
    store.connectionChanged('connected');
    await lastRead(reads).answer(admin);
    store.connectionChanged('stopped');

    store.connectionChanged('connecting');
    store.connectionChanged('connected');
    await lastRead(reads).answer(guest);

    expect(reads).toHaveLength(2);
    expect(store.getSnapshot().view).toEqual({ state: 'ready', session: guest });
  });

  it('cancels a read that a stop overtakes, and takes nothing from it', async () => {
    const { store, reads } = newStore();
    store.connectionChanged('connected');
    const read = lastRead(reads);

    store.connectionChanged('stopped');
    await read.answer(admin);

    expect(read.signal.aborted).toBe(true);
    expect(store.getSnapshot().view).toEqual({ state: 'waiting' });
  });

  it('takes only the last read when events come one after another', async () => {
    const { store, reads } = newStore();
    store.connectionChanged('connected');
    const first = lastRead(reads);

    store.sessionChanged();
    const second = lastRead(reads);
    await second.answer(guest);
    await first.answer(admin);

    expect(first.signal.aborted).toBe(true);
    expect(store.getSnapshot().view).toEqual({ state: 'ready', session: guest });
  });

  it('shows why the session could not be read, and reads it again for the user', async () => {
    const { store, reads } = newStore();
    store.connectionChanged('connected');

    await lastRead(reads).fail(new ApiError('network', 'Kiler does not answer.'));
    expect(store.getSnapshot().view).toEqual({ state: 'failed', message: 'Kiler does not answer.' });
    store.reload();
    await lastRead(reads).answer(guest);

    expect(store.getSnapshot().view).toEqual({ state: 'ready', session: guest });
  });

  it('takes the session of an action, over a read under way', async () => {
    const { store, reads } = newStore();
    store.connectionChanged('connected');
    const read = lastRead(reads);

    store.set(admin);
    await read.answer(guest);

    expect(read.signal.aborted).toBe(true);
    expect(store.getSnapshot().view).toEqual({ state: 'ready', session: admin });
  });

  it('goes back to the start on a stop of the page, so the next connect reads again', async () => {
    const { store, reads } = newStore();
    store.connectionChanged('connected');
    await lastRead(reads).answer(admin);

    store.stop();
    store.connectionChanged('connected');

    expect(reads).toHaveLength(2);
    expect(store.getSnapshot().view).toEqual({ state: 'reading' });
  });

  it('tells nothing more to a listener that unsubscribed', () => {
    const { store } = newStore();
    let calls = 0;
    const unsubscribe = store.subscribe(() => calls++);

    store.connectionChanged('connected');
    unsubscribe();
    store.connectionChanged('stopped');

    expect(calls).toBe(1);
  });
});

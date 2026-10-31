import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, type RecordData, type RecordField, type Session, type Template } from './api.ts';
import {
  fieldRows,
  hideAtSessionChange,
  keepsShown,
  offersEdit,
  offersSecret,
  padlockAction,
  pathParts,
  readsSession,
  shownSeconds,
  ShownSecrets,
  type SecretView,
} from './recordView.ts';
import { SessionStore } from './session.ts';

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

const reader: Session = { ...guest, signedIn: true, displayName: 'Demo Reader', levels: [0, 1] };
const editor: Session = { ...reader, displayName: 'Demo Editor', levels: [0, 1, 2], canEdit: true };

function field(key: string, rest: Partial<RecordField> = {}): RecordField {
  return { key, level: 0, levelName: null, kind: 'empty', value: null, ...rest } as RecordField;
}

const record: RecordData = {
  id: 'k3x9q2mf',
  template: 'db',
  version: 7,
  stash: [
    { id: 'a1b2c3d4', name: 'Payments' },
    { id: 'e5f6g7h8', name: null },
    { id: 'j9k0m1n2', name: 'Prod' },
  ],
  name: 'Payments production database',
  description: field('description'),
  tags: ['payments'],
  levels: [1, 3],
  fields: [field('host', { level: 1, kind: 'value', value: 'db01.example.test' })],
  createdBy: { account: 'demo.admin', name: 'Demo Admin' },
  createdAt: '2026-09-30T08:10:00Z',
  updatedBy: { account: 'demo.editor', name: null },
  updatedAt: '2026-10-21T11:32:00Z',
};

describe('the stash path', () => {
  it('links each part to its node, by the ids up to it', () => {
    expect(pathParts(record.stash)).toEqual([
      { id: 'a1b2c3d4', name: 'Payments', to: '/?stash=a1b2c3d4' },
      { id: 'e5f6g7h8', name: null, to: '/?stash=a1b2c3d4,e5f6g7h8' },
      { id: 'j9k0m1n2', name: 'Prod', to: '/?stash=a1b2c3d4,e5f6g7h8,j9k0m1n2' },
    ]);
  });

  it('gives no parts without a path', () => {
    expect(pathParts([])).toEqual([]);
  });
});

describe('who gets "Edit"', () => {
  it('is an editor, while the template of the record can be used', () => {
    expect(offersEdit(editor, record)).toBe(true);
  });

  it('is no one for a record whose template is missing or does not fit', () => {
    expect(offersEdit(editor, { ...record, fields: [] })).toBe(false);
  });

  it('is not a user without the right to edit, or a guest', () => {
    expect(offersEdit(reader, record)).toBe(false);
    expect(offersEdit(guest, record)).toBe(false);
    expect(offersEdit({ ...guest, canEdit: true }, record)).toBe(false);
  });
});

describe('the padlock and the secret', () => {
  it('offers "Sign in" beside a padlock to a guest', () => {
    expect(padlockAction(guest)).toBe('signIn');
  });

  it('offers nothing beside a padlock to a signed-in user, who has nothing to unlock', () => {
    expect(padlockAction(reader)).toBe('none');
    expect(padlockAction(editor)).toBe('none');
  });

  it('offers "Show" and "Copy" of a secret only to a signed-in user', () => {
    expect(offersSecret(guest)).toBe(false);
    expect(offersSecret(reader)).toBe(true);
  });
});

describe('the fields', () => {
  const template: Template = {
    id: 'db',
    name: 'Server / database',
    icon: 'database',
    fields: [
      ['host', 'Host', 'text'],
      ['link', 'Console', 'url'],
      ['local', 'Local console', 'url'],
      ['query', 'Query', 'code'],
      ['script', 'Script', 'code'],
      ['db', 'Database', 'ref'],
      ['port', 'Port', 'number'],
    ].map(([key, label, type]) => ({
      key: key ?? '',
      label: label ?? '',
      type: type as Template['fields'][number]['type'],
      level: 0,
      levelByEnv: null,
      required: false,
      filter: false,
      list: false,
      options: null,
      language: key === 'query' ? 'sql' : null,
      dialect: key === 'query' ? 'oracle' : null,
      languages: key === 'script' ? ['bash', 'powershell'] : null,
      refTemplate: null,
    })),
  };

  const rows = fieldRows(
    {
      ...record,
      fields: [
        field('host', { kind: 'value', value: 'db01.example.test' }),
        field('link', { kind: 'value', value: 'https://console.example.test/' }),
        field('local', { kind: 'value', value: 'http://127.0.0.1:8080/' }),
        field('query', { kind: 'value', value: 'SELECT 1 FROM dual' }),
        field('script', { kind: 'value', value: { language: 'bash', code: 'ls -la' } }),
        field('db', { kind: 'value', value: { id: 'p4q5r6s7', name: null } }),
        field('port', { kind: 'value', value: 1521 }),
        field('gone', { kind: 'value', value: 'a field that the template no longer has' }),
        field('password', { level: 3, kind: 'locked', levelName: 'Prod' }),
      ],
    },
    template,
  );
  const row = (key: string) => rows.find((entry) => entry.field.key === key);

  it('keeps the order of the record, with the labels and the types of the template', () => {
    expect(rows.map((entry) => entry.label)).toEqual([
      'Host',
      'Console',
      'Local console',
      'Query',
      'Script',
      'Database',
      'Port',
      'gone',
      'password',
    ]);
    expect(row('link')?.type).toBe('url');
    expect(row('gone')?.type).toBeUndefined();
  });

  it('copies the text of a value', () => {
    expect(row('host')?.copy).toBe('db01.example.test');
    expect(row('link')?.copy).toBe('https://console.example.test/');
    expect(row('port')?.copy).toBe('1521');
  });

  it('puts no "Copy" beside a url of this computer, a ref that has gone or a padlock', () => {
    expect(row('local')?.copy).toBeNull();
    expect(row('db')?.copy).toBeNull();
    expect(row('password')?.copy).toBeNull();
  });

  it('shows a code as code, with the language of its value or of the template', () => {
    expect(row('query')?.code).toEqual({ code: 'SELECT 1 FROM dual', language: 'sql', dialect: 'oracle' });
    expect(row('script')?.code).toEqual({ code: 'ls -la', language: 'bash', dialect: null });
    expect(row('query')?.copy).toBeNull();
    expect(row('host')?.code).toBeNull();
  });
});

describe('readsSession', () => {
  it('reads the session again after a 403 of Kiler', () => {
    expect(readsSession(new ApiError('refused', 'Sign in first.', 403))).toBe(true);
  });

  it('does not after another refusal or failure', () => {
    expect(readsSession(new ApiError('refused', 'The field has no value now.', 409))).toBe(false);
    expect(readsSession(new ApiError('status', 'Kiler answered 403.', 403))).toBe(false);
    expect(readsSession(new Error('no'))).toBe(false);
  });
});

describe('which record keeps the shown secrets', () => {
  it('is the same record in the same version, such as a read at a change of another record', () => {
    expect(keepsShown(record, { ...record })).toBe(true);
  });

  it('is not another version or another record', () => {
    expect(keepsShown(record, { ...record, version: 8 })).toBe(false);
    expect(keepsShown(record, { ...record, id: 'p4q5r6s7' })).toBe(false);
  });

  it('is not a read that failed, nor the first record of the page', () => {
    expect(keepsShown(record, null)).toBe(false);
    expect(keepsShown(null, record)).toBe(false);
  });
});

describe('the shown secrets', () => {
  /** A request of the value that the test answers. */
  interface Request {
    key: string;
    signal: AbortSignal;
    resolve(value: string): void;
    reject(error: unknown): void;
  }

  let requests: Request[];
  let refusals: unknown[];
  let secrets: ShownSecrets;
  const view = (key = 'password'): SecretView => ShownSecrets.viewOf(secrets.getSnapshot(), key);
  const settle = () => vi.advanceTimersByTimeAsync(0);
  const answer = async (value: string, index = requests.length - 1) => {
    requests[index]?.resolve(value);
    await settle();
  };

  beforeEach(() => {
    vi.useFakeTimers();
    requests = [];
    refusals = [];
    secrets = new ShownSecrets(
      (key, signal) =>
        new Promise<string>((resolve, reject) => {
          requests.push({ key, signal, resolve, reject });
        }),
      (error) => refusals.push(error),
    );
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('shows the value of its request', async () => {
    secrets.show('password');
    expect(view()).toEqual({ state: 'showing' });
    expect(requests.map((request) => request.key)).toEqual(['password']);

    await answer('made-up-1');
    expect(view()).toEqual({ state: 'shown', value: 'made-up-1' });
    expect(view('other')).toEqual({ state: 'hidden' });
  });

  it('hides a shown value after its time', async () => {
    secrets.show('password');
    await answer('made-up-1');

    await vi.advanceTimersByTimeAsync(shownSeconds * 1000 - 1);
    expect(view().state).toBe('shown');
    await vi.advanceTimersByTimeAsync(1);
    expect(view()).toEqual({ state: 'hidden' });
    expect(secrets.getSnapshot().size).toBe(0);
  });

  it('counts the time again from a new "Show"', async () => {
    secrets.show('password');
    await answer('made-up-1');
    await vi.advanceTimersByTimeAsync(20_000);

    secrets.show('password');
    await answer('made-up-2');
    await vi.advanceTimersByTimeAsync(20_000);
    expect(view()).toEqual({ state: 'shown', value: 'made-up-2' });
    await vi.advanceTimersByTimeAsync(10_000);
    expect(view()).toEqual({ state: 'hidden' });
  });

  it('hides a value on "Hide", and its timer goes with it', async () => {
    secrets.show('password');
    await answer('made-up-1');
    secrets.hide('password');
    expect(view()).toEqual({ state: 'hidden' });
    expect(vi.getTimerCount()).toBe(0);
  });

  it('counts only the last request of a field', async () => {
    secrets.show('password');
    secrets.show('password');
    expect(requests[0]?.signal.aborted).toBe(true);

    await answer('made-up-1', 0);
    expect(view()).toEqual({ state: 'showing' });
    await answer('made-up-2', 1);
    expect(view()).toEqual({ state: 'shown', value: 'made-up-2' });
  });

  it('shows a refusal by its sentence, and passes it on', async () => {
    const refusal = new ApiError('refused', 'Sign in first.', 403);
    secrets.show('password');
    requests[0]?.reject(refusal);
    await settle();
    expect(view()).toEqual({ state: 'failed', message: 'Sign in first.' });
    expect(refusals).toEqual([refusal]);
  });

  it('hides every value and aborts every request at hideAll', async () => {
    secrets.show('password');
    await answer('made-up-1');
    secrets.show('token');
    secrets.hideAll();

    expect(secrets.getSnapshot().size).toBe(0);
    expect(requests[1]?.signal.aborted).toBe(true);
    expect(vi.getTimerCount()).toBe(0);
    await answer('made-up-2', 1);
    expect(view('token')).toEqual({ state: 'hidden' });
  });

  it('keeps the shown values, and a request under way, while the page shows the same version of the record', async () => {
    secrets.showsRecord(record);
    secrets.show('password');
    await answer('made-up-1');
    secrets.show('token');

    secrets.showsRecord({ ...record });
    expect(requests[1]?.signal.aborted).toBe(false);
    await answer('made-up-2', 1);
    expect(view()).toEqual({ state: 'shown', value: 'made-up-1' });
    expect(view('token')).toEqual({ state: 'shown', value: 'made-up-2' });
  });

  it('hides every value, and aborts its request, when the page shows another version of the record', async () => {
    secrets.showsRecord(record);
    secrets.show('password');
    await answer('made-up-1');
    secrets.show('token');

    secrets.showsRecord({ ...record, version: 8 });
    expect(secrets.getSnapshot().size).toBe(0);
    expect(requests[1]?.signal.aborted).toBe(true);
    expect(vi.getTimerCount()).toBe(0);

    // The new version is the one the values belong to now: a read again of it keeps what shows after it.
    secrets.show('password');
    await answer('made-up-3');
    secrets.showsRecord({ ...record, version: 8 });
    expect(view()).toEqual({ state: 'shown', value: 'made-up-3' });
  });

  it('hides every value when the page shows no record, as after a read that failed', async () => {
    secrets.showsRecord(record);
    secrets.show('password');
    await answer('made-up-1');

    secrets.showsRecord(null);
    expect(view()).toEqual({ state: 'hidden' });
    expect(vi.getTimerCount()).toBe(0);
  });

  it('hides a shown value at a session event, and drops the answer of a request under way', async () => {
    const store = new SessionStore(() => Promise.resolve(editor));
    store.connectionChanged('connected');
    await settle();
    const leave = hideAtSessionChange(store, secrets);

    secrets.show('password');
    await answer('made-up-1');
    secrets.show('token');
    store.sessionChanged();
    expect(secrets.getSnapshot().size).toBe(0);
    expect(requests[1]?.signal.aborted).toBe(true);

    await answer('made-up-2', 1);
    await settle();
    expect(view('token')).toEqual({ state: 'hidden' });

    leave();
    secrets.show('password');
    await answer('made-up-3');
    store.sessionChanged();
    expect(view()).toEqual({ state: 'shown', value: 'made-up-3' });
  });
});

import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  activityHeader,
  addUser,
  ApiError,
  changeLevel,
  changePassword,
  createRecord,
  createStash,
  deleteRecord,
  editRecord,
  getAdminLevels,
  getAdminUsers,
  getData,
  getHealth,
  getHistory,
  getJson,
  getLevels,
  getRecord,
  getRecords,
  getSession,
  getStashes,
  getTags,
  getTemplates,
  getTrash,
  lockSession,
  messageOf,
  noActivity,
  recordsSearch,
  renameLevel,
  requestSecret,
  restoreFromTrash,
  restoreVersion,
  runWizard,
  setRights,
  signIn,
  type EditRecordRequest,
  type NewRecordRequest,
  type Session,
} from './api.ts';
import { changes } from './changes.ts';
import { texts } from './texts.ts';

function stubFetch(answer: () => Promise<Response>) {
  const fetch = vi.fn<typeof globalThis.fetch>(answer);
  vi.stubGlobal('fetch', fetch);
  return fetch;
}

function json(body: unknown, status = 200): Promise<Response> {
  return Promise.resolve(
    new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }),
  );
}

async function failure(promise: Promise<unknown>): Promise<ApiError> {
  const error = await promise.then(
    () => undefined,
    (error: unknown) => error,
  );
  expect(error).toBeInstanceOf(ApiError);
  return error as ApiError;
}

afterEach(() => {
  vi.unstubAllGlobals();
  // The changes of the page are one for the module, so a test that notes one ends what it started.
  changes.input();
});

describe('the mark of a read that follows a change (M6.11)', () => {
  it('goes on each read from a "changed" event until the input of the user', async () => {
    const fetch = stubFetch(() => json({ version: '0.1.0' }));

    changes.note();
    await getHealth();
    await getHealth();
    changes.input();
    await getHealth();

    expect(fetch.mock.calls.map(([, init]) => init?.headers)).toEqual([
      { Accept: 'application/json', [activityHeader]: noActivity },
      { Accept: 'application/json', [activityHeader]: noActivity },
      { Accept: 'application/json' },
    ]);
  });

  it('never goes on a write, which Kiler counts as activity anyway', async () => {
    const fetch = stubFetch(() => json({ id: 'a1b2c3d4', name: 'Reports' }, 201));

    changes.note();
    await createStash('Reports');

    expect(fetch.mock.calls[0]?.[1]?.headers).toEqual({
      Accept: 'application/json',
      'Content-Type': 'application/json',
    });
  });
});

describe('getHealth', () => {
  it('returns the version from GET /api/health of this origin, with the cookie', async () => {
    const fetch = stubFetch(() => json({ version: '0.1.0' }));

    expect(await getHealth()).toEqual({ version: '0.1.0' });

    expect(fetch).toHaveBeenCalledOnce();
    const [path, init] = fetch.mock.calls[0] ?? [];
    expect(path).toBe('/api/health');
    expect(init).toMatchObject({ method: 'GET', credentials: 'same-origin', cache: 'no-store' });
    expect(init?.headers).toEqual({ Accept: 'application/json' });
  });

  it('turns 401 into a message instead of breaking', async () => {
    stubFetch(() => Promise.resolve(new Response(null, { status: 401 })));

    const error = await failure(getHealth());

    expect(error.kind).toBe('unauthorized');
    expect(error.status).toBe(401);
    expect(error.message).toBe(texts.errors.unauthorized);
    expect(messageOf(error)).toBe(texts.errors.unauthorized);
  });

  it('turns 503 into "stopping"', async () => {
    stubFetch(() => Promise.resolve(new Response(null, { status: 503 })));

    const error = await failure(getHealth());

    expect(error.kind).toBe('stopping');
    expect(error.message).toBe(texts.errors.stopping);
  });

  it.each([400, 403, 404, 500])('turns %i into an error with the status', async (status) => {
    stubFetch(() => Promise.resolve(new Response('not json', { status })));

    const error = await failure(getHealth());

    expect(error.kind).toBe('status');
    expect(error.status).toBe(status);
    expect(error.message).toBe(texts.errors.status(status));
  });

  it('turns a failed connection into "does not answer"', async () => {
    stubFetch(() => Promise.reject(new TypeError('Failed to fetch')));

    const error = await failure(getHealth());

    expect(error.kind).toBe('network');
    expect(error.message).toBe(texts.errors.network);
  });

  it.each([
    ['not JSON', () => Promise.resolve(new Response('<html>', { status: 200 }))],
    ['no version', () => json({ name: 'Kiler' })],
    ['a version that is not a string', () => json({ version: 1 })],
    ['null', () => json(null)],
  ])('turns an answer with %s into "invalid"', async (_, answer) => {
    stubFetch(answer);

    const error = await failure(getHealth());

    expect(error.kind).toBe('invalid');
    expect(error.message).toBe(texts.errors.invalid);
  });

  it('passes an abort by the caller on as it is', async () => {
    const abort = new AbortController();
    const aborted = new DOMException('Aborted', 'AbortError');
    stubFetch(() => {
      abort.abort();
      return Promise.reject(aborted);
    });

    await expect(getHealth(abort.signal)).rejects.toBe(aborted);
  });
});

describe('getJson', () => {
  it.each(['/other', '/apiary', 'api/health', 'http://127.0.0.1:5050/api/health', '//evil.example/api/health'])(
    'refuses %s, which is not an API path of this origin, without a request',
    async (path) => {
      const fetch = stubFetch(() => json({}));

      await expect(getJson(path, (body) => body)).rejects.toThrow('Not a Kiler API path');
      expect(fetch).not.toHaveBeenCalled();
    },
  );
});

const session: Session = {
  account: 'demo.user',
  signedIn: true,
  displayName: 'Demo User',
  levels: [0, 1, 2],
  canEdit: true,
  isAdmin: false,
  mustChangePassword: false,
  guestMode: false,
};

describe('refusals', () => {
  it.each([400, 403, 409])('turns %i with { error } into "refused" with the sentence of Kiler', async (status) => {
    stubFetch(() => json({ error: 'The password is wrong.' }, status));

    const error = await failure(signIn('demo-password'));

    expect(error.kind).toBe('refused');
    expect(error.status).toBe(status);
    expect(error.message).toBe('The password is wrong.');
    expect(error.field).toBeNull();
    expect(error.current).toBeNull();
  });

  it.each([
    ['no body', () => Promise.resolve(new Response(null, { status: 403 }))],
    ['an error that is not a string', () => json({ error: 1 }, 403)],
    ['an empty error', () => json({ error: '' }, 409)],
  ])('turns a refusal with %s into an error with the status', async (_, answer) => {
    stubFetch(answer);

    const error = await failure(getSession());

    expect(error.kind).toBe('status');
    expect(error.message).toBe(texts.errors.status(error.status ?? 0));
  });
});

describe('the session', () => {
  it('reads GET /api/session', async () => {
    const fetch = stubFetch(() => json(session));

    expect(await getSession()).toEqual(session);
    expect(fetch.mock.calls[0]?.[0]).toBe('/api/session');
  });

  it('signs in with POST of the password as JSON, and gives the session of the answer', async () => {
    const fetch = stubFetch(() => json(session));

    expect(await signIn('demo-password')).toEqual(session);

    const [path, init] = fetch.mock.calls[0] ?? [];
    expect(path).toBe('/api/session');
    expect(init).toMatchObject({ method: 'POST', credentials: 'same-origin', body: '{"password":"demo-password"}' });
    expect(init?.headers).toEqual({ Accept: 'application/json', 'Content-Type': 'application/json' });
  });

  it('locks with DELETE, without a body', async () => {
    const locked = { ...session, signedIn: false, displayName: null, levels: [], canEdit: false };
    const fetch = stubFetch(() => json(locked));

    expect(await lockSession()).toEqual(locked);

    const [path, init] = fetch.mock.calls[0] ?? [];
    expect(path).toBe('/api/session');
    expect(init).toMatchObject({ method: 'DELETE', body: undefined });
    expect(init?.headers).toEqual({ Accept: 'application/json' });
  });

  it.each([
    ['the forced change, with no current password', { current: null, password: 'new-password' }],
    ['a voluntary change, with the current password', { current: 'old-password', password: 'new-password' }],
  ])('changes the password with POST for %s, and gives the session of the answer', async (_, request) => {
    const fetch = stubFetch(() => json(session));

    expect(await changePassword(request)).toEqual(session);

    const [path, init] = fetch.mock.calls[0] ?? [];
    expect(path).toBe('/api/session/password');
    expect(init).toMatchObject({ method: 'POST', body: JSON.stringify(request) });
  });

  it.each([
    ['no account member', { ...session, account: undefined }],
    ['levels that are not whole numbers', { ...session, levels: [0, '1'] }],
    ['levels that are not a list', { ...session, levels: 2 }],
    ['signedIn that is not true or false', { ...session, signedIn: 'yes' }],
    ['a list', [session]],
  ])('turns a session with %s into "invalid"', async (_, body) => {
    stubFetch(() => json(body));

    expect((await failure(getSession())).kind).toBe('invalid');
  });
});

describe('getData', () => {
  it('reads the state of the data', async () => {
    const data = { state: 'open', folder: 'D:\\Demo\\data', levels: null, settings: 'The file is missing.' };
    stubFetch(() => json(data));

    expect(await getData()).toEqual(data);
  });

  it('turns a state that this page does not know into "invalid"', async () => {
    stubFetch(() => json({ state: 'sleeping', folder: null, levels: null, settings: null }));

    expect((await failure(getData())).kind).toBe('invalid');
  });
});

describe('runWizard', () => {
  const request = {
    levels: ['Guest', 'Team'],
    displayName: 'Demo Admin',
    password: 'demo-password',
    guestMode: false,
    autoLockMinutes: 30,
  };

  it('sends the request with POST and gives the recovery key of the answer', async () => {
    const fetch = stubFetch(() => json({ recoveryKey: 'ABCD-EFGH' }));

    expect(await runWizard(request)).toBe('ABCD-EFGH');

    const [path, init] = fetch.mock.calls[0] ?? [];
    expect(path).toBe('/api/wizard');
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual(request);
  });

  it('turns an answer without the recovery key into "invalid"', async () => {
    stubFetch(() => json({}));

    expect((await failure(runWizard(request))).kind).toBe('invalid');
  });
});

describe('the admin actions', () => {
  const user = {
    account: 'demo.user',
    displayName: 'Demo User',
    level: 2,
    canEdit: true,
    isAdmin: false,
    mustChangePassword: true,
    keysMatchLevel: true,
    invalid: null,
  };
  const damaged = { ...user, displayName: null, level: null, canEdit: false, invalid: 'The file is damaged.' };
  const levels = [
    { level: 0, name: 'Guest' },
    { level: 1, name: 'Team' },
  ];

  it('reads the users with the warning, a file that does not fit too', async () => {
    const fetch = stubFetch(() => json({ users: [user, damaged], fewAdmins: true }));

    expect(await getAdminUsers()).toEqual({ users: [user, damaged], fewAdmins: true });
    expect(fetch.mock.calls[0]?.[0]).toBe('/api/admin/users');
  });

  it('reads the levels', async () => {
    const fetch = stubFetch(() => json({ levels }));

    expect(await getAdminLevels()).toEqual(levels);
    expect(fetch.mock.calls[0]?.[0]).toBe('/api/admin/levels');
  });

  it('adds a user with POST, and gives the temporary password of the answer', async () => {
    const request = { account: 'DEMO\\Demo.User', displayName: 'Demo User', level: 2, canEdit: true };
    const fetch = stubFetch(() => json({ user, temporaryPassword: 'a1b2c3d4e5f6g7h8' }));

    expect(await addUser(request)).toEqual({ user, temporaryPassword: 'a1b2c3d4e5f6g7h8' });

    const [path, init] = fetch.mock.calls[0] ?? [];
    expect(path).toBe('/api/admin/users');
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual(request);
  });

  it('changes the level with PUT of the level, and the rights with PUT of both rights', async () => {
    const fetch = stubFetch(() => json({ user, temporaryPassword: null }));

    expect(await changeLevel('demo.user', 2)).toEqual({ user, temporaryPassword: null });
    expect(await setRights('demo.user', { canEdit: true, isAdmin: false })).toEqual({ user, temporaryPassword: null });

    const [levelPath, levelInit] = fetch.mock.calls[0] ?? [];
    expect(levelPath).toBe('/api/admin/users/demo.user/level');
    expect(levelInit).toMatchObject({ method: 'PUT', body: '{"level":2}' });
    const [rightsPath, rightsInit] = fetch.mock.calls[1] ?? [];
    expect(rightsPath).toBe('/api/admin/users/demo.user/rights');
    expect(rightsInit).toMatchObject({ method: 'PUT', body: '{"canEdit":true,"isAdmin":false}' });
  });

  it('puts the account into the path as one segment', async () => {
    const fetch = stubFetch(() => json({ user, temporaryPassword: null }));

    await changeLevel('../levels/1', 1);

    expect(fetch.mock.calls[0]?.[0]).toBe('/api/admin/users/..%2Flevels%2F1/level');
  });

  it('renames a level with PUT of the name, and gives the levels of the answer', async () => {
    const fetch = stubFetch(() => json({ levels }));

    expect(await renameLevel(1, 'Team')).toEqual(levels);

    const [path, init] = fetch.mock.calls[0] ?? [];
    expect(path).toBe('/api/admin/levels/1');
    expect(init).toMatchObject({ method: 'PUT', body: '{"name":"Team"}' });
  });

  it.each([
    [
      'a user without keysMatchLevel',
      () => getAdminUsers(),
      { users: [{ ...user, keysMatchLevel: undefined }], fewAdmins: false },
    ],
    ['users without fewAdmins', () => getAdminUsers(), { users: [user] }],
    ['a level that is not a whole number', () => getAdminLevels(), { levels: [{ level: '0', name: 'Guest' }] }],
    [
      'an answer without the user',
      () => addUser({ account: 'a', displayName: 'A', level: 1, canEdit: false }),
      { temporaryPassword: 'x' },
    ],
    ['an empty temporary password', () => changeLevel('demo.user', 1), { user, temporaryPassword: '' }],
  ])('turns %s into "invalid"', async (_, request, body) => {
    stubFetch(() => json(body));

    expect((await failure(request())).kind).toBe('invalid');
  });
});

// The records of docs/api.md, made up.
const editor = { account: 'demo.editor', name: 'Demo Editor' };
const description = {
  key: 'description',
  level: 0,
  kind: 'value',
  value: 'The main database of the payments',
  levelName: null,
};
const listItem = {
  id: 'k3x9q2mf',
  template: 'db',
  version: 7,
  stash: [
    { id: 'a1b2c3d4', name: 'Payments' },
    { id: 'e5f6g7h8', name: null },
  ],
  name: 'Payments production database',
  description,
  tags: ['payments'],
  fields: [{ key: 'host', level: 1, kind: 'locked', value: null, levelName: 'Team' }],
  updatedBy: editor,
  updatedAt: '2026-10-21T11:32:00Z',
};
const record = {
  ...listItem,
  levels: [1, 3],
  fields: [
    { key: 'env', level: 0, kind: 'value', value: 'prod', levelName: null },
    { key: 'port', level: 1, kind: 'value', value: 1521, levelName: null },
    { key: 'tns', level: 1, kind: 'empty', value: null, levelName: null },
    { key: 'query', level: 1, kind: 'value', value: { language: 'sql', code: 'select 1 from dual' }, levelName: null },
    { key: 'database', level: 0, kind: 'value', value: { id: 'm4n5p6q7', name: null }, levelName: null },
    { key: 'password', level: 2, kind: 'secret', value: null, levelName: null },
    { key: 'user', level: 3, kind: 'locked', value: null, levelName: 'Prod' },
    { key: 'notes', level: 1, kind: 'unreadable', value: null, levelName: null },
  ],
  createdBy: { account: 'demo.admin', name: null },
  createdAt: '2026-09-30T08:10:00Z',
};
const trashItem = { ...listItem, levels: [1] };
const versions = [
  { version: 8, updatedBy: editor, updatedAt: '2026-10-21T11:32:00Z', levels: [1, 3] },
  { version: 6, updatedBy: { account: 'demo.admin', name: null }, updatedAt: '2026-10-20T09:00:00Z', levels: [] },
];

function templateField(key: string, type: string, settings: object = {}) {
  return {
    key,
    label: key,
    type,
    level: 1,
    levelByEnv: null,
    required: false,
    filter: false,
    list: false,
    options: null,
    language: null,
    dialect: null,
    languages: null,
    refTemplate: null,
    ...settings,
  };
}

const template = {
  id: 'db',
  name: 'Server / database',
  icon: 'database',
  fields: [
    templateField('env', 'select', { level: 0, filter: true, options: ['prod', 'test', 'dev'] }),
    templateField('password', 'secret', { level: null, levelByEnv: { prod: 3, test: 2, dev: 1 } }),
    templateField('query', 'code', { required: true, list: true, languages: ['sql', 'bash'] }),
    templateField('script', 'code', { language: 'sql', dialect: 'oracle' }),
    templateField('database', 'ref', { refTemplate: 'db' }),
  ],
};
const templates = { templates: [template], invalid: [{ id: 'old', reason: 'The template has no fields.' }] };
const recordsAnswer = {
  records: [listItem],
  tree: [
    { id: 'a1b2c3d4', name: 'Payments', count: 30, children: [{ id: 'e5f6g7h8', name: null, count: 9, children: [] }] },
  ],
  facets: {
    templates: [{ id: 'db', count: 12 }],
    tags: [{ tag: 'payments', count: 5 }],
    fields: [{ key: 'env', values: [{ value: 'prod', count: 4 }] }],
  },
};
const stashes = [
  { id: 'e5f6g7h8', name: 'Prod', used: true },
  { id: 'j9k0m1n2', name: 'Test', used: false },
];

const newRecord: NewRecordRequest = {
  template: 'db',
  stash: ['a1b2c3d4', 'e5f6g7h8'],
  name: 'Payments production database',
  description: 'The main database of the payments',
  tags: ['payments'],
  fields: {
    env: 'prod',
    port: 1521,
    query: { language: 'sql', code: 'select 1 from dual' },
    database: 'm4n5p6q7',
    password: 'made-up-password',
    tns: null,
  },
  levels: { description: 2 },
};
const edit: EditRecordRequest = {
  version: 7,
  stash: ['a1b2c3d4'],
  name: 'Payments production database',
  description: null,
  tags: [],
  fields: { env: 'prod', port: 1522 },
  levels: { port: 2 },
};

describe('the reads of the records', () => {
  it.each<[string, () => Promise<unknown>, string, unknown, unknown]>([
    ['the templates', () => getTemplates(), '/api/templates', templates, templates],
    [
      'the levels',
      () => getLevels(),
      '/api/levels',
      { levels: [{ level: 0, name: 'Guest' }] },
      [{ level: 0, name: 'Guest' }],
    ],
    ['the records', () => getRecords({}), '/api/records', recordsAnswer, recordsAnswer],
    ['a record', () => getRecord('k3x9q2mf'), '/api/records/k3x9q2mf', record, record],
    ['the stashes of position 1', () => getStashes([]), '/api/stashes', { stashes }, stashes],
    [
      'the stashes of position 3',
      () => getStashes(['a1b2c3d4', 'e5f6g7h8']),
      '/api/stashes?path=a1b2c3d4,e5f6g7h8',
      { stashes },
      stashes,
    ],
    ['the tags', () => getTags(), '/api/tags', { tags: ['payments', 'reports'] }, ['payments', 'reports']],
    ['the trash', () => getTrash(), '/api/trash', { records: [trashItem] }, [trashItem]],
    ['the history', () => getHistory('k3x9q2mf'), '/api/records/k3x9q2mf/history', { versions }, versions],
  ])('reads %s with GET', async (_, read, path, body, expected) => {
    const fetch = stubFetch(() => json(body));

    expect(await read()).toEqual(expected);

    const [called, init] = fetch.mock.calls[0] ?? [];
    expect(called).toBe(path);
    expect(init).toMatchObject({ method: 'GET', body: undefined });
  });

  const withField = (field: object) => ({ ...record, fields: [field] });

  it.each<[string, () => Promise<unknown>, unknown]>([
    [
      'a field of a template of a type that this page does not know',
      () => getTemplates(),
      { templates: [{ ...template, fields: [templateField('colour', 'color')] }], invalid: [] },
    ],
    [
      'a level by env that is not a whole number',
      () => getTemplates(),
      { templates: [{ ...template, fields: [templateField('password', 'secret', { levelByEnv: { prod: '3' } })] }] },
    ],
    ['templates without the invalid ones', () => getTemplates(), { templates: [template] }],
    ['a level without its name', () => getLevels(), { levels: [{ level: 0 }] }],
    [
      'a record of the list without its version',
      () => getRecords({}),
      { ...recordsAnswer, records: [{ ...listItem, version: undefined }] },
    ],
    [
      'a node of the tree without its children',
      () => getRecords({}),
      { ...recordsAnswer, tree: [{ id: 'a1b2c3d4', name: 'Payments', count: 1 }] },
    ],
    ['facets without the fields', () => getRecords({}), { ...recordsAnswer, facets: { templates: [], tags: [] } }],
    [
      'a field of a kind that this page does not know',
      () => getRecord('k3x9q2mf'),
      withField({ key: 'host', level: 1, kind: 'hidden', value: null, levelName: null }),
    ],
    [
      'a value without the value',
      () => getRecord('k3x9q2mf'),
      withField({ key: 'host', level: 1, kind: 'value', value: null, levelName: null }),
    ],
    [
      'a secret with its value',
      () => getRecord('k3x9q2mf'),
      withField({ key: 'password', level: 2, kind: 'secret', value: 'made-up-password', levelName: null }),
    ],
    [
      'a value that is true or false',
      () => getRecord('k3x9q2mf'),
      withField({ key: 'host', level: 1, kind: 'value', value: true, levelName: null }),
    ],
    ['a record without who created it', () => getRecord('k3x9q2mf'), { ...record, createdBy: undefined }],
    ['a date that is not a date', () => getRecord('k3x9q2mf'), { ...record, updatedAt: 'yesterday' }],
    ['levels that are not whole numbers', () => getRecord('k3x9q2mf'), { ...record, levels: [1, 'Prod'] }],
    ['a stash without used', () => getStashes([]), { stashes: [{ id: 'a1b2c3d4', name: 'Payments' }] }],
    ['a tag that is not a text', () => getTags(), { tags: [1] }],
    ['an item of the trash without its levels', () => getTrash(), { records: [listItem] }],
    ['a version without its date', () => getHistory('k3x9q2mf'), { versions: [{ ...versions[0], updatedAt: null }] }],
    ['an answer of a secret without the value', () => requestSecret('k3x9q2mf', 'password', 'show'), {}],
    ['a new stash without its name', () => createStash('Reports'), { id: 'p3q4r5s6', name: null }],
    ['a record of a write without its fields', () => editRecord('k3x9q2mf', edit), { ...record, fields: undefined }],
    ['a deleted record without its levels', () => deleteRecord('k3x9q2mf', 7), listItem],
  ])('turns %s into "invalid"', async (_, request, body) => {
    stubFetch(() => json(body));

    expect((await failure(request())).kind).toBe('invalid');
  });
});

describe('recordsSearch', () => {
  it('leaves every blank part out', () => {
    expect(recordsSearch({})).toBe('');
    expect(recordsSearch({ q: ' ', stash: [], template: '', tags: [''], fields: { env: '' } })).toBe('');
  });

  it('writes each part, the stash path with its commas, a tag once per tag and a field by its key', () => {
    const search = recordsSearch({
      q: 'pay db',
      stash: ['a1b2c3d4', 'e5f6g7h8'],
      template: 'db',
      tags: ['payments', '', 'reports'],
      fields: { env: 'prod', region: '' },
    });

    expect(search).toBe('?q=pay%20db&stash=a1b2c3d4,e5f6g7h8&template=db&tag=payments&tag=reports&field.env=prod');
  });

  it('encodes a value, so the address reads it back as it was', () => {
    const query = { q: 'база & db=1', tags: ['a+b'], fields: { env: '#prod?' } };

    const read = new URLSearchParams(recordsSearch(query));

    expect(read.get('q')).toBe(query.q);
    expect(read.getAll('tag')).toEqual(query.tags);
    expect(read.get('field.env')).toBe('#prod?');
  });

  it('is the query of GET /api/records', async () => {
    const fetch = stubFetch(() => json(recordsAnswer));

    await getRecords({ q: 'db', stash: ['a1b2c3d4'], tags: ['payments'] });

    expect(fetch.mock.calls[0]?.[0]).toBe('/api/records?q=db&stash=a1b2c3d4&tag=payments');
  });
});

describe('the writes of the records', () => {
  it.each(['show', 'copy'] as const)(
    'asks for a secret to %s with POST of its field and use, and gives the value of the answer',
    async (use) => {
      const fetch = stubFetch(() => json({ value: 'made-up-password' }));

      expect(await requestSecret('k3x9q2mf', 'password', use)).toBe('made-up-password');

      const [path, init] = fetch.mock.calls[0] ?? [];
      expect(path).toBe('/api/records/k3x9q2mf/secret');
      expect(init).toMatchObject({ method: 'POST', body: JSON.stringify({ field: 'password', use }) });
    },
  );

  it('creates a record with POST, and gives the record of the answer', async () => {
    const fetch = stubFetch(() => json(record, 201));

    expect(await createRecord(newRecord)).toEqual(record);

    const [path, init] = fetch.mock.calls[0] ?? [];
    expect(path).toBe('/api/records');
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual(newRecord);
  });

  it('edits a record with PUT over the version that the editor read', async () => {
    const fetch = stubFetch(() => json(record));

    expect(await editRecord('k3x9q2mf', edit)).toEqual(record);

    const [path, init] = fetch.mock.calls[0] ?? [];
    expect(path).toBe('/api/records/k3x9q2mf');
    expect(init?.method).toBe('PUT');
    expect(JSON.parse(String(init?.body))).toEqual(edit);
  });

  it('creates a stash with POST of its name', async () => {
    const fetch = stubFetch(() => json({ id: 'p3q4r5s6', name: 'Reports' }, 201));

    expect(await createStash('Reports')).toEqual({ id: 'p3q4r5s6', name: 'Reports' });

    const [path, init] = fetch.mock.calls[0] ?? [];
    expect(path).toBe('/api/stashes');
    expect(init).toMatchObject({ method: 'POST', body: '{"name":"Reports"}' });
  });

  it('deletes a record with DELETE over its version, without a body, and gives its trash item', async () => {
    const fetch = stubFetch(() => json(trashItem));

    expect(await deleteRecord('k3x9q2mf', 7)).toEqual(trashItem);

    const [path, init] = fetch.mock.calls[0] ?? [];
    expect(path).toBe('/api/records/k3x9q2mf?version=7');
    expect(init).toMatchObject({ method: 'DELETE', body: undefined });
  });

  it('restores a record from the trash with POST, without a body', async () => {
    const fetch = stubFetch(() => json(record));

    expect(await restoreFromTrash('k3x9q2mf')).toEqual(record);

    const [path, init] = fetch.mock.calls[0] ?? [];
    expect(path).toBe('/api/trash/k3x9q2mf/restore');
    expect(init).toMatchObject({ method: 'POST', body: undefined });
    expect(init?.headers).toEqual({ Accept: 'application/json' });
  });

  it('restores an old version with POST of the current version', async () => {
    const fetch = stubFetch(() => json(record));

    expect(await restoreVersion('k3x9q2mf', 6, 8)).toEqual(record);

    const [path, init] = fetch.mock.calls[0] ?? [];
    expect(path).toBe('/api/records/k3x9q2mf/history/6/restore');
    expect(init).toMatchObject({ method: 'POST', body: '{"version":8}' });
  });

  it('puts an id into the path as one segment', async () => {
    const fetch = stubFetch(() => json(record));

    await getRecord('a/b?c#d');

    expect(fetch.mock.calls[0]?.[0]).toBe('/api/records/a%2Fb%3Fc%23d');
  });

  it.each([
    ['a record', () => getRecord('..')],
    ['the secret', () => requestSecret('..', 'password', 'copy')],
    ['a restore from the trash', () => restoreFromTrash('.')],
  ])('refuses %s with an id that the browser would resolve to another path, without a request', async (_, request) => {
    const fetch = stubFetch(() => json(record));

    await expect(request()).rejects.toThrow('Not a Kiler API path');
    expect(fetch).not.toHaveBeenCalled();
  });
});

describe('the refusals of a write', () => {
  it.each([
    ['the key of the field', 'host', 'host'],
    ['no field', null, null],
  ])('keeps %s of a 400', async (_, field, kept) => {
    stubFetch(() => json({ error: 'The value does not fit.', field }, 400));

    const error = await failure(createRecord(newRecord));

    expect(error.kind).toBe('refused');
    expect(error.status).toBe(400);
    expect(error.message).toBe('The value does not fit.');
    expect(error.field).toBe(kept);
    expect(error.current).toBeNull();
  });

  it('keeps the record of a conflict, as it is now', async () => {
    const current = { ...record, version: 8 };
    stubFetch(() => json({ error: 'The record has been changed meanwhile.', current }, 409));

    const error = await failure(editRecord('k3x9q2mf', edit));

    expect(error.kind).toBe('refused');
    expect(error.status).toBe(409);
    expect(error.message).toBe('The record has been changed meanwhile.');
    expect(error.current).toEqual(current);
    expect(error.field).toBeNull();
  });

  it('reads a 409 without current as a refusal like the others', async () => {
    stubFetch(() => json({ error: 'The record is being written now. Try again in a moment.' }, 409));

    const error = await failure(deleteRecord('k3x9q2mf', 7));

    expect(error.kind).toBe('refused');
    expect(error.message).toBe('The record is being written now. Try again in a moment.');
    expect(error.current).toBeNull();
  });

  it.each([
    [
      'a current that is not a record',
      { error: 'The record has been changed meanwhile.', current: { id: 'k3x9q2mf' } },
    ],
    ['a field that is not a text', { error: 'The value does not fit.', field: 1 }],
  ])('turns a refusal with %s into "invalid"', async (_, body) => {
    stubFetch(() => json(body, 409));

    const error = await failure(editRecord('k3x9q2mf', edit));

    expect(error.kind).toBe('invalid');
    expect(error.status).toBe(409);
  });
});

describe('messageOf', () => {
  it('gives a general message for an error that is not from a request', () => {
    expect(messageOf(new Error('internal details'))).toBe(texts.errors.unexpected);
  });
});

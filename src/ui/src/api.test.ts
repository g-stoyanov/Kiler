import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  ApiError,
  changePassword,
  getData,
  getHealth,
  getJson,
  getSession,
  lockSession,
  messageOf,
  runWizard,
  signIn,
  type Session,
} from './api.ts';
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

describe('messageOf', () => {
  it('gives a general message for an error that is not from a request', () => {
    expect(messageOf(new Error('internal details'))).toBe(texts.errors.unexpected);
  });
});

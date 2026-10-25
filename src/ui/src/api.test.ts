import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, getHealth, getJson, messageOf } from './api.ts';
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

describe('messageOf', () => {
  it('gives a general message for an error that is not from a request', () => {
    expect(messageOf(new Error('internal details'))).toBe(texts.errors.unexpected);
  });
});

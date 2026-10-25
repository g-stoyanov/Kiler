import { texts } from './texts.ts';

/** What went wrong with a request, so the page can tell the user. */
export type ApiErrorKind = 'unauthorized' | 'stopping' | 'status' | 'network' | 'invalid';

/** A failed request to Kiler. The message is for the user. */
export class ApiError extends Error {
  readonly kind: ApiErrorKind;
  readonly status: number | undefined;

  constructor(kind: ApiErrorKind, message: string, status?: number) {
    super(message);
    this.name = 'ApiError';
    this.kind = kind;
    this.status = status;
  }
}

/** The answer of `GET /api/health`. */
export interface Health {
  version: string;
}

/**
 * `GET` of a Kiler API path. This is the one place that turns every failure into an {@link ApiError} with a message
 * for the user, so the page shows it instead of breaking. An abort by the caller is passed on as it is.
 *
 * Only paths of this origin under `/api/`: the cookie of the token must not go anywhere else.
 */
export async function getJson<T>(path: string, read: (body: unknown) => T, signal?: AbortSignal): Promise<T> {
  if (!path.startsWith('/api/')) {
    throw new Error(`Not a Kiler API path: ${path}`);
  }

  let response: Response;
  try {
    response = await fetch(path, {
      method: 'GET',
      headers: { Accept: 'application/json' },
      credentials: 'same-origin',
      cache: 'no-store',
      signal,
    });
  } catch (error) {
    if (signal?.aborted) {
      throw error;
    }

    throw new ApiError('network', texts.errors.network);
  }

  if (response.status === 401) {
    throw new ApiError('unauthorized', texts.errors.unauthorized, response.status);
  }

  if (response.status === 503) {
    throw new ApiError('stopping', texts.errors.stopping, response.status);
  }

  if (!response.ok) {
    throw new ApiError('status', texts.errors.status(response.status), response.status);
  }

  try {
    return read(await response.json());
  } catch (error) {
    if (signal?.aborted) {
      throw error;
    }

    throw new ApiError('invalid', texts.errors.invalid, response.status);
  }
}

export function getHealth(signal?: AbortSignal): Promise<Health> {
  return getJson('/api/health', readHealth, signal);
}

/** The message for the user about any error from a request. */
export function messageOf(error: unknown): string {
  return error instanceof ApiError ? error.message : texts.errors.unexpected;
}

function readHealth(body: unknown): Health {
  if (typeof body === 'object' && body !== null && 'version' in body && typeof body.version === 'string') {
    return { version: body.version };
  }

  throw new TypeError('Not an answer of /api/health.');
}

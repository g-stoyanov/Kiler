import { texts } from './texts.ts';

/**
 * What went wrong with a request, so the page can tell the user. `refused` is an answer of Kiler that says why in a
 * sentence for the page (`{ "error" }`): a value that does not fit (400), no right (403) or not now (409).
 */
export type ApiErrorKind = 'unauthorized' | 'stopping' | 'refused' | 'status' | 'network' | 'invalid';

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

/** What the page shows of the session (`GET /api/session`, M4.4). One per Kiler process, shared by all its pages. */
export interface Session {
  /** The account of this Kiler; null when the Windows user name cannot be one, so no one can sign in. */
  account: string | null;
  signedIn: boolean;
  /** The display name of the user file; null without a session. */
  displayName: string | null;
  /** The levels that the page reads: `[]` with no session and no guest mode, `[0]` for a guest. */
  levels: number[];
  canEdit: boolean;
  /** Whether the user file has `isAdmin`; the admin actions also need the key of every level. */
  isAdmin: boolean;
  /** A temporary password, which is changed before anything else (M4.5a). */
  mustChangePassword: boolean;
  /** Without a session, the page opens as a guest, who reads level 0, with "Sign in". */
  guestMode: boolean;
}

/** The states of the data (`DataState` of Kiler.App, host.md, "Data"). */
export const dataStates = [
  'noFolder',
  'opening',
  'missing',
  'unusable',
  'inUse',
  'indexUnreadable',
  'failed',
  'open',
  'offline',
] as const;

export type DataState = (typeof dataStates)[number];

/** The answer of `GET /api/data` (M4.2). */
export interface DataInfo {
  state: DataState;
  /** The full path of the data folder; null when none was chosen. */
  folder: string | null;
  /** Why `levels.json` of the local copy cannot be used; null when it can, and while the copy is not open. */
  levels: string | null;
  /** The same for `settings.json`. */
  settings: string | null;
}

/** The reason of a file of the team that the local copy does not have: `DataApi.MissingFile` of Kiler.App. */
export const missingFile = 'The file is missing.';

/** What the wizard sends (`POST /api/wizard`, M4.3). The page checks that the password was typed twice the same. */
export interface WizardRequest {
  /** The names of the levels 0 to the top level, in order; the admin gets the top level. */
  levels: string[];
  displayName: string;
  password: string;
  guestMode: boolean;
  autoLockMinutes: number;
}

/** What the change of password sends (`POST /api/session/password`, M4.5a). The page checks the new password twice. */
export interface PasswordRequest {
  /** The current password; null in the forced change after a temporary password (`mustChangePassword`). */
  current: string | null;
  password: string;
}

/**
 * Kiler's sentences of a refused change of password that the page acts on, beyond showing them
 * (`PasswordApi.Message` of Kiler.App): the two 403s about the password itself, after which the page keeps its form,
 * and the user file that has changed since the sign-in, after which the page offers a new sign-in.
 */
export const passwordRefusals = {
  wrongPassword: 'The current password is wrong.',
  levelNotUnlocked:
    'A level of your user file did not unlock with your password, and a new password would lose it. Contact an admin.',
  userChanged: 'Your user file has changed since you signed in. Sign in again.',
} as const;

/**
 * The 403 of `levels.json` with other checks than at the sign-in (`AccessRules.Message(AccessRefusal.LevelsChanged)` of
 * Kiler.App), after which the admin screens offer a new sign-in, as reading the session again would not help.
 */
export const levelsChanged = 'levels.json has changed since you signed in. Sign in again.';

/** A user file as the admin's list shows it (`GET /api/admin/users`, M4.6). */
export interface AdminUser {
  account: string;
  /** Null, as `level`, when the file does not fit (`invalid`). */
  displayName: string | null;
  /** The level that the file says; the keys are what the user reads (design 7.8). */
  level: number | null;
  canEdit: boolean;
  isAdmin: boolean;
  /** The user has a temporary password, which they change at their next sign-in. */
  mustChangePassword: boolean;
  /** False is the mark: the file has a level without its key, or a key above it. */
  keysMatchLevel: boolean;
  /** Why the file does not fit or is damaged; null when it fits. */
  invalid: string | null;
}

export interface AdminUsers {
  /** In the order of the accounts. */
  users: AdminUser[];
  /** The team has fewer than two admins, so the list warns (design 7.7). */
  fewAdmins: boolean;
}

/** A level of `levels.json` (`GET /api/admin/levels`). */
export interface AdminLevel {
  level: number;
  name: string;
}

/** A new user (`POST /api/admin/users`). The account is the Windows user name, `DOMAIN\` and capitals allowed. */
export interface NewUserRequest {
  account: string;
  displayName: string;
  /** From 1 to the top level. */
  level: number;
  canEdit: boolean;
}

/** The editor and admin rights, sent whole: a member left out would take that right away (M4.6). */
export interface RightsRequest {
  canEdit: boolean;
  isAdmin: boolean;
}

/** The answer of an action on a user: the user file as it left it, and the temporary password that only it gives. */
export interface AdminUserAnswer {
  user: AdminUser;
  /** Null when the action gave no keys: the rights other than making an admin. */
  temporaryPassword: string | null;
}

type Method = 'GET' | 'POST' | 'PUT' | 'DELETE';

/**
 * A request to a Kiler API path, with a JSON body or none, and a JSON answer. This is the one place that turns every
 * failure into an {@link ApiError} with a message for the user, so the page shows it instead of breaking. An abort by
 * the caller is passed on as it is.
 *
 * Only paths of this origin under `/api/`: the cookie of the token must not go anywhere else.
 */
async function requestJson<T>(
  method: Method,
  path: string,
  body: unknown,
  read: (body: unknown) => T,
  signal?: AbortSignal,
): Promise<T> {
  if (!path.startsWith('/api/')) {
    throw new Error(`Not a Kiler API path: ${path}`);
  }

  let response: Response;
  try {
    response = await fetch(path, {
      method,
      headers:
        body === undefined
          ? { Accept: 'application/json' }
          : { Accept: 'application/json', 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
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
    const refusal = await readRefusal(response);
    throw refusal === undefined
      ? new ApiError('status', texts.errors.status(response.status), response.status)
      : new ApiError('refused', refusal, response.status);
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

/** `GET` of a Kiler API path ({@link requestJson}). */
export function getJson<T>(path: string, read: (body: unknown) => T, signal?: AbortSignal): Promise<T> {
  return requestJson('GET', path, undefined, read, signal);
}

export function getHealth(signal?: AbortSignal): Promise<Health> {
  return getJson('/api/health', readHealth, signal);
}

export function getSession(signal?: AbortSignal): Promise<Session> {
  return getJson('/api/session', readSession, signal);
}

/** Signs in with the password of the user file of this Kiler's account, and answers with the session (M4.4). */
export function signIn(password: string): Promise<Session> {
  return requestJson('POST', '/api/session', { password }, readSession);
}

/** "Lock": the sign-out and the lock alike (M4.4). Answers with the session, now without the keys. */
export function lockSession(): Promise<Session> {
  return requestJson('DELETE', '/api/session', undefined, readSession);
}

/** Changes the password of the session, and answers with the session, now without `mustChangePassword` (M4.5a). */
export function changePassword(request: PasswordRequest): Promise<Session> {
  return requestJson('POST', '/api/session/password', request, readSession);
}

export function getData(signal?: AbortSignal): Promise<DataInfo> {
  return getJson('/api/data', readData, signal);
}

/** Makes a new Kiler and answers with its recovery key, which only this answer gives (M4.3). */
export function runWizard(request: WizardRequest): Promise<string> {
  return requestJson('POST', '/api/wizard', request, readRecoveryKey);
}

export function getAdminUsers(signal?: AbortSignal): Promise<AdminUsers> {
  return getJson('/api/admin/users', readAdminUsers, signal);
}

export function getAdminLevels(signal?: AbortSignal): Promise<AdminLevel[]> {
  return getJson('/api/admin/levels', readAdminLevels, signal);
}

/** Adds a user, whose temporary password only the answer gives (M4.6). */
export function addUser(request: NewUserRequest): Promise<AdminUserAnswer> {
  return requestJson('POST', '/api/admin/users', request, readAdminUserAnswer);
}

/**
 * A new temporary password with the keys of the levels 1 to `level`: a change of level, and a forgotten password with
 * the user's level again. The admin gives the level, never Kiler from the user file, which its user can write (M4.6).
 */
export function changeLevel(account: string, level: number): Promise<AdminUserAnswer> {
  return requestJson('PUT', `${userPath(account)}/level`, { level }, readAdminUserAnswer);
}

/** The editor and admin rights; making an admin gives the top level with a new temporary password. */
export function setRights(account: string, rights: RightsRequest): Promise<AdminUserAnswer> {
  return requestJson('PUT', `${userPath(account)}/rights`, rights, readAdminUserAnswer);
}

/** Renames a level, and answers with the levels. */
export function renameLevel(level: number, name: string): Promise<AdminLevel[]> {
  return requestJson('PUT', `/api/admin/levels/${String(level)}`, { name }, readAdminLevels);
}

function userPath(account: string): string {
  return `/api/admin/users/${encodeURIComponent(account)}`;
}

/** The message for the user about any error from a request. */
export function messageOf(error: unknown): string {
  return error instanceof ApiError ? error.message : texts.errors.unexpected;
}

/** The sentence of a refusal (`{ "error" }`), or undefined for an answer without one, such as the 403 of the guard. */
async function readRefusal(response: Response): Promise<string | undefined> {
  try {
    const body: unknown = await response.json();
    return isObject(body) && typeof body.error === 'string' && body.error !== '' ? body.error : undefined;
  } catch {
    return undefined;
  }
}

function isObject(body: unknown): body is Record<string, unknown> {
  return typeof body === 'object' && body !== null && !Array.isArray(body);
}

function isTextOrNull(value: unknown): value is string | null {
  return value === null || typeof value === 'string';
}

function readHealth(body: unknown): Health {
  if (isObject(body) && typeof body.version === 'string') {
    return { version: body.version };
  }

  throw new TypeError('Not an answer of /api/health.');
}

function readSession(body: unknown): Session {
  if (isObject(body)) {
    const { account, signedIn, displayName, levels, canEdit, isAdmin, mustChangePassword, guestMode } = body;
    if (
      isTextOrNull(account) &&
      typeof signedIn === 'boolean' &&
      isTextOrNull(displayName) &&
      Array.isArray(levels) &&
      levels.every((level: unknown) => Number.isInteger(level)) &&
      typeof canEdit === 'boolean' &&
      typeof isAdmin === 'boolean' &&
      typeof mustChangePassword === 'boolean' &&
      typeof guestMode === 'boolean'
    ) {
      return {
        account,
        signedIn,
        displayName,
        levels: levels as number[],
        canEdit,
        isAdmin,
        mustChangePassword,
        guestMode,
      };
    }
  }

  throw new TypeError('Not an answer of /api/session.');
}

function readData(body: unknown): DataInfo {
  if (isObject(body)) {
    const { state, folder, levels, settings } = body;
    const known = dataStates.find((name) => name === state);
    if (known !== undefined && isTextOrNull(folder) && isTextOrNull(levels) && isTextOrNull(settings)) {
      return { state: known, folder, levels, settings };
    }
  }

  throw new TypeError('Not an answer of /api/data.');
}

function readRecoveryKey(body: unknown): string {
  if (isObject(body) && typeof body.recoveryKey === 'string' && body.recoveryKey !== '') {
    return body.recoveryKey;
  }

  throw new TypeError('Not an answer of /api/wizard.');
}

function readAdminUser(body: unknown): AdminUser {
  if (isObject(body)) {
    const { account, displayName, level, canEdit, isAdmin, mustChangePassword, keysMatchLevel, invalid } = body;
    if (
      typeof account === 'string' &&
      isTextOrNull(displayName) &&
      (level === null || Number.isInteger(level)) &&
      typeof canEdit === 'boolean' &&
      typeof isAdmin === 'boolean' &&
      typeof mustChangePassword === 'boolean' &&
      typeof keysMatchLevel === 'boolean' &&
      isTextOrNull(invalid)
    ) {
      return {
        account,
        displayName,
        level: level as number | null,
        canEdit,
        isAdmin,
        mustChangePassword,
        keysMatchLevel,
        invalid,
      };
    }
  }

  throw new TypeError('Not a user of /api/admin/users.');
}

function readAdminUsers(body: unknown): AdminUsers {
  if (isObject(body) && Array.isArray(body.users) && typeof body.fewAdmins === 'boolean') {
    return { users: body.users.map(readAdminUser), fewAdmins: body.fewAdmins };
  }

  throw new TypeError('Not an answer of /api/admin/users.');
}

function readAdminUserAnswer(body: unknown): AdminUserAnswer {
  if (isObject(body) && isTextOrNull(body.temporaryPassword) && body.temporaryPassword !== '') {
    return { user: readAdminUser(body.user), temporaryPassword: body.temporaryPassword };
  }

  throw new TypeError('Not an answer of an admin action.');
}

function readAdminLevels(body: unknown): AdminLevel[] {
  if (isObject(body) && Array.isArray(body.levels)) {
    return body.levels.map((entry: unknown) => {
      if (isObject(entry) && Number.isInteger(entry.level) && typeof entry.name === 'string') {
        return { level: entry.level as number, name: entry.name };
      }

      throw new TypeError('Not a level of /api/admin/levels.');
    });
  }

  throw new TypeError('Not an answer of /api/admin/levels.');
}

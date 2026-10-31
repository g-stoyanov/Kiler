import { changes } from './changes.ts';
import { texts } from './texts.ts';

/**
 * The header of a read that follows a `changed` event before the user's next input (changes.ts, decided by the user in
 * M6.11), with {@link noActivity}: Kiler does not count such a `GET` as activity for the automatic lock. The names are
 * `AccessRules.ActivityHeader` and `AccessRules.NoActivity` in Kiler.App, so a change of one changes the other.
 */
export const activityHeader = 'Kiler-Activity';
export const noActivity = 'none';

/**
 * What went wrong with a request, so the page can tell the user. `refused` is an answer of Kiler that says why in a
 * sentence for the page (`{ "error" }`): a value that does not fit (400), no right (403), not there (404) or not now
 * (409); a refusal of a write also keeps its `field` and `current` ({@link Refusal}).
 */
export type ApiErrorKind = 'unauthorized' | 'stopping' | 'refused' | 'status' | 'network' | 'invalid';

/** What a refusal of a write carries beyond its sentence, for the page to act on (docs/api.md, "Conventions"). */
export interface Refusal {
  /** The key of what does not fit (`stash`, `name`, `description`, `tags` or a field of the template); null for none. */
  field: string | null;
  /** The record as it is now, of the 409 of a write over an older version (docs/api.md, "Conflict"); null otherwise. */
  current: RecordData | null;
}

/** A failed request to Kiler. The message is for the user; a refusal also keeps its `field` and its `current`. */
export class ApiError extends Error {
  readonly kind: ApiErrorKind;
  readonly status: number | undefined;
  readonly field: string | null;
  readonly current: RecordData | null;

  constructor(kind: ApiErrorKind, message: string, status?: number, refusal?: Refusal) {
    super(message);
    this.name = 'ApiError';
    this.kind = kind;
    this.status = status;
    this.field = refusal?.field ?? null;
    this.current = refusal?.current ?? null;
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

/** A level of `levels.json`, with its name: of `GET /api/levels` (M5.2), and of `GET /api/admin/levels`. */
export interface LevelName {
  level: number;
  name: string;
}

/** A level of the admin's screen of the levels (`GET /api/admin/levels`). */
export type AdminLevel = LevelName;

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

/** Who made a version of a record (docs/api.md, "Author"). */
export interface Author {
  /** The account that Kiler wrote. */
  account: string;
  /** The display name of its user file; null when the team has no user file of it that fits. */
  name: string | null;
}

/** A stash of the path of a record (docs/api.md, "A stash of a path"). */
export interface PathStash {
  id: string;
  /** Null when the stash cannot be read: it is missing, or it does not fit. The record still shows at its place. */
  name: string | null;
}

/** A new stash (`POST /api/stashes`). */
export interface Stash {
  id: string;
  name: string;
}

/** A stash that the form offers at a position of the path (`GET /api/stashes`). */
export interface StashChoice extends Stash {
  /** A record has it at that position under that path; these come first. */
  used: boolean;
}

/** The value of a `code` whose template gives `languages`: the code with the language chosen (design 6.3). */
export interface CodeValue {
  language: string;
  code: string;
}

/** A `ref` in an answer: the record that it points to. */
export interface RefValue {
  id: string;
  /** Null when that record has gone: deleted, or never there. */
  name: string | null;
}

/**
 * A value of a field in an answer (docs/api.md, "Values by type"). It keeps the shape that it was written in, which a
 * template changed since can make another than its type says, so the page tells the shape by the value itself.
 */
export type RecordValue = string | number | CodeValue | RefValue;

const fieldKinds = ['value', 'empty', 'secret', 'locked', 'unreadable'] as const;

/** What the session reads of a field (docs/api.md, "Field"). */
export type FieldKind = (typeof fieldKinds)[number];

/**
 * A field of a record, and its description (docs/api.md, "Field"). Only a `value` has its value: that of a `secret` comes
 * only from {@link requestSecret}.
 */
export type RecordField = {
  /** The key of the field in the template; `description` for the description. */
  key: string;
  /** The level of the field in this record; without a value, the one that a value would get. */
  level: number;
  /** The name of the level of a `locked` field, for "🔒 requires level X"; null otherwise, or when `levels.json` cannot be read. */
  levelName: string | null;
} & ({ kind: 'value'; value: RecordValue } | { kind: Exclude<FieldKind, 'value'>; value: null });

/** An item of `GET /api/records` (docs/api.md, "List item"). */
export interface ListItem {
  id: string;
  /** The id of its template. */
  template: string;
  /** The version of its file, which an edit and a delete are over. */
  version: number;
  /** The stashes of its path, from the first. */
  stash: PathStash[];
  name: string;
  description: RecordField;
  tags: string[];
  /** The fields of the template with `list`, in its order, never a `secret`; a long value whole, which the page shortens. */
  fields: RecordField[];
  updatedBy: Author;
  /** UTC in ISO 8601, which the page shows in local time. */
  updatedAt: string;
}

/** An item of `GET /api/trash`, and the answer of a delete (docs/api.md, "Trash item"). */
export interface TrashItem extends ListItem {
  /** The levels of its groups, by which the page offers the restore. */
  levels: number[];
}

/**
 * A record (docs/api.md, "Record"): of `GET /api/records/{id}`, of the writes, and the `current` of a conflict. Its
 * `fields` are every field of the template, in its order; `[]` when the template is missing or does not fit, and then
 * an edit is refused.
 */
export interface RecordData extends ListItem {
  /** The levels of its groups, by which the page offers the delete and the restores. */
  levels: number[];
  createdBy: Author;
  createdAt: string;
}

/** A version of a record (`GET /api/records/{id}/history`, docs/api.md, "Version"). */
export interface RecordVersion {
  version: number;
  /** Who made that version, and when. */
  updatedBy: Author;
  updatedAt: string;
  /** The levels of its groups, by which the page offers its restore. */
  levels: number[];
}

const fieldTypes = [
  'text',
  'multiline',
  'markdown',
  'doc',
  'select',
  'number',
  'date',
  'url',
  'code',
  'ref',
  'secret',
] as const;

/** The types of the fields of design 6.4. */
export type FieldType = (typeof fieldTypes)[number];

/** A field of a template (docs/api.md, "Template"). A setting that does not belong to its type is null. */
export interface TemplateField {
  key: string;
  label: string;
  type: FieldType;
  /** The default level; null when the field has only `levelByEnv`. */
  level: number | null;
  /** The level by the value of the field `env` (docs/api.md, "Levels of a field"). */
  levelByEnv: Record<string, number> | null;
  required: boolean;
  /** Whether the field is a filter of the list. */
  filter: boolean;
  /** Whether the list shows the field. */
  list: boolean;
  /** The values of a `select`. */
  options: string[] | null;
  /** The fixed language of a `code`, and its dialect. */
  language: string | null;
  dialect: string | null;
  /** The languages that a `code` takes, whose value is then a {@link CodeValue}. */
  languages: string[] | null;
  /** The template of the records that a `ref` points to. */
  refTemplate: string | null;
}

export interface Template {
  /** The name of its file in `templates\`. */
  id: string;
  name: string;
  /** The name of the icon, which the page takes from its own set. */
  icon: string;
  /** In the order of the template. */
  fields: TemplateField[];
}

/** A file of `templates\` that does not fit. */
export interface InvalidTemplate {
  id: string;
  /** Why; it names a JSON member and never repeats a value of the share. */
  reason: string;
}

/** The answer of `GET /api/templates`. */
export interface Templates {
  /** The templates that fit, by name. */
  templates: Template[];
  invalid: InvalidTemplate[];
}

/**
 * The query of `GET /api/records` (docs/api.md), which the main view also keeps in its address (M6 decisions). Every part
 * is optional, and a blank one is left out ({@link recordsSearch}).
 */
export interface RecordsQuery {
  /** The search text: a record matches when it has every word. */
  q?: string;
  /** The ids of the path of the chosen node, 1 to 4. */
  stash?: string[];
  /** The id of a template. */
  template?: string;
  /** The tags, every one of which a record has. */
  tags?: string[];
  /** A value of a field with `filter`, by key. */
  fields?: Record<string, string>;
}

/** A node of the stash tree of `GET /api/records`. */
export interface StashNode {
  id: string;
  /** Null when the stash cannot be read; such a node is last. */
  name: string | null;
  /** The records at the node and under it. */
  count: number;
  children: StashNode[];
}

export interface TemplateCount {
  /** The id of a template. */
  id: string;
  count: number;
}

export interface TagCount {
  tag: string;
  count: number;
}

export interface ValueCount {
  /** The text of a value that the session reads. */
  value: string;
  count: number;
}

export interface FieldFacet {
  /** The key of a field with `filter`. */
  key: string;
  values: ValueCount[];
}

/** The counts of the filters: each counts without its own filter, but the tags with the tags chosen. */
export interface Facets {
  templates: TemplateCount[];
  tags: TagCount[];
  fields: FieldFacet[];
}

/** The answer of `GET /api/records`. */
export interface RecordsAnswer {
  /** The records that match everything, by name. */
  records: ListItem[];
  /** The nodes of the paths of the records that match everything but `stash`, by name. */
  tree: StashNode[];
  facets: Facets;
}

/** What the value of a `secret` is asked for, which the audit of Prod records after the exam (docs/api.md). */
export type SecretUse = 'show' | 'copy';

/** A value of a field in a write (docs/api.md, "Values by type"): a `ref` is the id of its record; `null` and `""` are no value. */
export type RequestValue = string | number | CodeValue | null;

/** What a new record and an edit send (docs/api.md, `POST /api/records` and `PUT /api/records/{id}`). */
export interface RecordRequest {
  /** The ids of the stashes of the path, 1 to 4. */
  stash: string[];
  name: string;
  description: string | null;
  tags: string[];
  /** The fields of the template by key. One left out has no value, but a `secret` left out of an edit keeps its value. */
  fields: Record<string, RequestValue>;
  /** The levels asked, by key (docs/api.md, "Levels of a field"); an edit sends only those that the editor changed. */
  levels: Record<string, number>;
}

export interface NewRecordRequest extends RecordRequest {
  /** The id of a template that fits. */
  template: string;
}

export interface EditRecordRequest extends RecordRequest {
  /** The version that the editor read, which the edit is over. */
  version: number;
}

type Method = 'GET' | 'POST' | 'PUT' | 'DELETE';

/**
 * A request to a Kiler API path, with a JSON body or none, and a JSON answer. This is the one place that turns every
 * failure into an {@link ApiError} with a message for the user, so the page shows it instead of breaking. An abort by
 * the caller is passed on as it is.
 *
 * Only paths of this origin under `/api/`: the cookie of the token must not go anywhere else. A path with a `.` or `..`
 * segment, such as one with an id of a `ref` that a file changed by hand gives, is refused too, as the browser would
 * send it to another path.
 */
async function requestJson<T>(
  method: Method,
  path: string,
  body: unknown,
  read: (body: unknown) => T,
  signal?: AbortSignal,
): Promise<T> {
  const segments = (path.split('?', 1)[0] ?? '').split('/');
  if (!path.startsWith('/api/') || segments.some((segment) => segment === '.' || segment === '..')) {
    throw new Error(`Not a Kiler API path: ${path}`);
  }

  const headers: Record<string, string> = { Accept: 'application/json' };
  if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  // Only a read: Kiler counts a write and the request of a secret as activity anyway.
  if (method === 'GET' && changes.follows) {
    headers[activityHeader] = noActivity;
  }

  let response: Response;
  try {
    response = await fetch(path, {
      method,
      headers,
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
    throw await refusalOf(response);
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
  return getJson('/api/admin/levels', readLevels, signal);
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
  return requestJson('PUT', `/api/admin/levels/${String(level)}`, { name }, readLevels);
}

function userPath(account: string): string {
  return `/api/admin/users/${encodeURIComponent(account)}`;
}

export function getTemplates(signal?: AbortSignal): Promise<Templates> {
  return getJson('/api/templates', readTemplates, signal);
}

/** The levels 0 to the top level with their names, for the form and the filters. */
export function getLevels(signal?: AbortSignal): Promise<LevelName[]> {
  return getJson('/api/levels', readLevels, signal);
}

/** The search, the filters, the stash tree and the facets (M5.6). */
export function getRecords(query: RecordsQuery, signal?: AbortSignal): Promise<RecordsAnswer> {
  return getJson(`/api/records${recordsSearch(query)}`, readRecordsAnswer, signal);
}

/**
 * The query of `GET /api/records` as the search part of an address, `?` and all, or `''` without any part: the blank
 * parts left out, `tag` once per tag and `field.<key>` per field. The main view writes it into its address too, so the
 * ids of `stash` keep their commas.
 */
export function recordsSearch(query: RecordsQuery): string {
  const parts: string[] = [];
  const add = (name: string, value: string | undefined) => {
    if (value !== undefined && value.trim() !== '') {
      parts.push(`${encodeURIComponent(name)}=${encodeURIComponent(value)}`);
    }
  };

  add('q', query.q);
  if (query.stash !== undefined && query.stash.length > 0) {
    parts.push(`stash=${query.stash.map((id) => encodeURIComponent(id)).join(',')}`);
  }

  add('template', query.template);
  for (const tag of query.tags ?? []) {
    add('tag', tag);
  }

  for (const [key, value] of Object.entries(query.fields ?? {})) {
    add(`field.${key}`, value);
  }

  return parts.length === 0 ? '' : `?${parts.join('&')}`;
}

/** A record; a 404 when it is not there, or is in the trash. */
export function getRecord(id: string, signal?: AbortSignal): Promise<RecordData> {
  return getJson(recordPath(id), readRecordData, signal);
}

/** The stashes for the position of the path after `path` (0 to 3 ids), those that a record has there first. */
export function getStashes(path: string[], signal?: AbortSignal): Promise<StashChoice[]> {
  const query = path.length === 0 ? '' : `?path=${path.map((id) => encodeURIComponent(id)).join(',')}`;
  return getJson(`/api/stashes${query}`, readStashChoices, signal);
}

/** The tags of the records, for the suggestions of the form. */
export function getTags(signal?: AbortSignal): Promise<string[]> {
  return getJson('/api/tags', readTags, signal);
}

export function getTrash(signal?: AbortSignal): Promise<TrashItem[]> {
  return getJson('/api/trash', readTrash, signal);
}

/** The versions of a record, the newest first, the current one with them. */
export function getHistory(id: string, signal?: AbortSignal): Promise<RecordVersion[]> {
  return getJson(`${recordPath(id)}/history`, readHistory, signal);
}

/**
 * The value of a `secret`, for "Show" or "Copy" as `use` says: only this answer has it, and the page keeps it only while
 * it shows or copies it (kiler-ui, "Secret values"). A guest is refused.
 */
export function requestSecret(id: string, field: string, use: SecretUse, signal?: AbortSignal): Promise<string> {
  return requestJson('POST', `${recordPath(id)}/secret`, { field, use }, readSecret, signal);
}

/** A new record, and the record as this session reads it. */
export function createRecord(request: NewRecordRequest): Promise<RecordData> {
  return requestJson('POST', '/api/records', request, readRecordData);
}

/** An edit over the version that the editor read; over an older one, the refusal has the record as it is now (`current`). */
export function editRecord(id: string, request: EditRecordRequest): Promise<RecordData> {
  return requestJson('PUT', recordPath(id), request, readRecordData);
}

/** A new stash. After any 409 the page reads the stashes again, as a stash of that name may be there (M5.1). */
export function createStash(name: string): Promise<Stash> {
  return requestJson('POST', '/api/stashes', { name }, readStash);
}

/** Moves a record to the trash over the version that the editor read, and gives its trash item. */
export function deleteRecord(id: string, version: number): Promise<TrashItem> {
  return requestJson('DELETE', `${recordPath(id)}?version=${String(version)}`, undefined, readTrashItem);
}

/** Brings a record back from the trash as it was deleted. */
export function restoreFromTrash(id: string): Promise<RecordData> {
  return requestJson('POST', `/api/trash/${encodeURIComponent(id)}/restore`, undefined, readRecordData);
}

/** Makes the old version `old` of a record the current one again, over the `current` version that the editor read. */
export function restoreVersion(id: string, old: number, current: number): Promise<RecordData> {
  return requestJson('POST', `${recordPath(id)}/history/${String(old)}/restore`, { version: current }, readRecordData);
}

/** The path of a record, with its id as one segment. */
function recordPath(id: string): string {
  return `/api/records/${encodeURIComponent(id)}`;
}

/** The message for the user about any error from a request. */
export function messageOf(error: unknown): string {
  return error instanceof ApiError ? error.message : texts.errors.unexpected;
}

/**
 * The error of an answer that is not OK. A refusal (`{ "error" }`) is `refused`, with Kiler's sentence and what the page
 * acts on ({@link Refusal}); one whose `field` or `current` does not fit is `invalid`. An answer without a sentence, such
 * as the 403 of the guard, is its status.
 */
async function refusalOf(response: Response): Promise<ApiError> {
  let body: unknown;
  try {
    body = await response.json();
  } catch {
    body = undefined;
  }

  if (!isObject(body) || typeof body.error !== 'string' || body.error === '') {
    return new ApiError('status', texts.errors.status(response.status), response.status);
  }

  try {
    return new ApiError('refused', body.error, response.status, readRefusal(body));
  } catch {
    return new ApiError('invalid', texts.errors.invalid, response.status);
  }
}

/** A 409 without `current` is a refusal like the others. */
function readRefusal(body: Record<string, unknown>): Refusal {
  const { field = null, current = null } = body;
  if (isTextOrNull(field)) {
    return { field, current: current === null ? null : readRecordData(current) };
  }

  throw new TypeError('Not a refusal of a write.');
}

function isObject(body: unknown): body is Record<string, unknown> {
  return typeof body === 'object' && body !== null && !Array.isArray(body);
}

function isTextOrNull(value: unknown): value is string | null {
  return value === null || typeof value === 'string';
}

function isWholeNumber(value: unknown): value is number {
  return Number.isInteger(value);
}

/** A date and time of Kiler, in UTC (docs/api.md, "Conventions"). */
function isDate(value: unknown): value is string {
  return typeof value === 'string' && !Number.isNaN(Date.parse(value));
}

function readList<T>(body: unknown, read: (item: unknown) => T): T[] {
  if (Array.isArray(body)) {
    return body.map((item: unknown) => read(item));
  }

  throw new TypeError('Not a list.');
}

function readText(body: unknown): string {
  if (typeof body === 'string') {
    return body;
  }

  throw new TypeError('Not a text.');
}

function readTextsOrNull(body: unknown): string[] | null {
  return body === null ? null : readList(body, readText);
}

function readLevel(body: unknown): number {
  if (isWholeNumber(body)) {
    return body;
  }

  throw new TypeError('Not a level.');
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

/** The levels of `GET /api/levels` and of `GET /api/admin/levels`. */
function readLevels(body: unknown): LevelName[] {
  if (isObject(body) && Array.isArray(body.levels)) {
    return body.levels.map((entry: unknown) => {
      if (isObject(entry) && Number.isInteger(entry.level) && typeof entry.name === 'string') {
        return { level: entry.level as number, name: entry.name };
      }

      throw new TypeError('Not a level of levels.json.');
    });
  }

  throw new TypeError('Not an answer of the levels.');
}

function readTemplates(body: unknown): Templates {
  if (isObject(body)) {
    return { templates: readList(body.templates, readTemplate), invalid: readList(body.invalid, readInvalidTemplate) };
  }

  throw new TypeError('Not an answer of /api/templates.');
}

function readTemplate(body: unknown): Template {
  if (isObject(body) && typeof body.id === 'string' && typeof body.name === 'string' && typeof body.icon === 'string') {
    return { id: body.id, name: body.name, icon: body.icon, fields: readList(body.fields, readTemplateField) };
  }

  throw new TypeError('Not a template.');
}

function readTemplateField(body: unknown): TemplateField {
  if (isObject(body)) {
    const { key, label, type, level, levelByEnv, required, filter, list } = body;
    const { options, language, dialect, languages, refTemplate } = body;
    const known = fieldTypes.find((name) => name === type);
    if (
      typeof key === 'string' &&
      typeof label === 'string' &&
      known !== undefined &&
      (level === null || isWholeNumber(level)) &&
      typeof required === 'boolean' &&
      typeof filter === 'boolean' &&
      typeof list === 'boolean' &&
      isTextOrNull(language) &&
      isTextOrNull(dialect) &&
      isTextOrNull(refTemplate)
    ) {
      return {
        key,
        label,
        type: known,
        level,
        levelByEnv: levelByEnv === null ? null : readLevelByEnv(levelByEnv),
        required,
        filter,
        list,
        options: readTextsOrNull(options),
        language,
        dialect,
        languages: readTextsOrNull(languages),
        refTemplate,
      };
    }
  }

  throw new TypeError('Not a field of a template.');
}

function readLevelByEnv(body: unknown): Record<string, number> {
  if (isObject(body)) {
    return Object.fromEntries(Object.entries(body).map(([env, level]) => [env, readLevel(level)]));
  }

  throw new TypeError('Not the levels by env of a field.');
}

function readInvalidTemplate(body: unknown): InvalidTemplate {
  if (isObject(body) && typeof body.id === 'string' && typeof body.reason === 'string') {
    return { id: body.id, reason: body.reason };
  }

  throw new TypeError('Not a template that does not fit.');
}

function readAuthor(body: unknown): Author {
  if (isObject(body) && typeof body.account === 'string' && isTextOrNull(body.name)) {
    return { account: body.account, name: body.name };
  }

  throw new TypeError('Not an author.');
}

function readPathStash(body: unknown): PathStash {
  if (isObject(body) && typeof body.id === 'string' && isTextOrNull(body.name)) {
    return { id: body.id, name: body.name };
  }

  throw new TypeError('Not a stash of a path.');
}

function readValue(body: unknown): RecordValue {
  if (typeof body === 'string' || (typeof body === 'number' && Number.isFinite(body))) {
    return body;
  }

  if (isObject(body)) {
    if (typeof body.language === 'string' && typeof body.code === 'string') {
      return { language: body.language, code: body.code };
    }

    if (typeof body.id === 'string' && isTextOrNull(body.name)) {
      return { id: body.id, name: body.name };
    }
  }

  throw new TypeError('Not a value of a field.');
}

/** A field; one of another kind than `value` with a value does not fit, so no answer but the secret's can hold one. */
function readField(body: unknown): RecordField {
  if (isObject(body)) {
    const { key, level, kind, value, levelName } = body;
    const known = fieldKinds.find((name) => name === kind);
    if (typeof key === 'string' && isWholeNumber(level) && known !== undefined && isTextOrNull(levelName)) {
      if (known === 'value') {
        return { key, level, kind: known, value: readValue(value), levelName };
      }

      if (value === null) {
        return { key, level, kind: known, value, levelName };
      }
    }
  }

  throw new TypeError('Not a field of a record.');
}

function readListItem(body: unknown): ListItem {
  if (isObject(body)) {
    const { id, template, version, name, updatedAt } = body;
    if (
      typeof id === 'string' &&
      typeof template === 'string' &&
      isWholeNumber(version) &&
      typeof name === 'string' &&
      isDate(updatedAt)
    ) {
      return {
        id,
        template,
        version,
        stash: readList(body.stash, readPathStash),
        name,
        description: readField(body.description),
        tags: readList(body.tags, readText),
        fields: readList(body.fields, readField),
        updatedBy: readAuthor(body.updatedBy),
        updatedAt,
      };
    }
  }

  throw new TypeError('Not a record of a list.');
}

function readTrashItem(body: unknown): TrashItem {
  if (isObject(body)) {
    return { ...readListItem(body), levels: readList(body.levels, readLevel) };
  }

  throw new TypeError('Not a record of the trash.');
}

function readRecordData(body: unknown): RecordData {
  if (isObject(body) && isDate(body.createdAt)) {
    return {
      ...readListItem(body),
      levels: readList(body.levels, readLevel),
      createdBy: readAuthor(body.createdBy),
      createdAt: body.createdAt,
    };
  }

  throw new TypeError('Not a record.');
}

function readStashNode(body: unknown): StashNode {
  if (isObject(body) && typeof body.id === 'string' && isTextOrNull(body.name) && isWholeNumber(body.count)) {
    return { id: body.id, name: body.name, count: body.count, children: readList(body.children, readStashNode) };
  }

  throw new TypeError('Not a node of the stash tree.');
}

function readTemplateCount(body: unknown): TemplateCount {
  if (isObject(body) && typeof body.id === 'string' && isWholeNumber(body.count)) {
    return { id: body.id, count: body.count };
  }

  throw new TypeError('Not a count of a template.');
}

function readTagCount(body: unknown): TagCount {
  if (isObject(body) && typeof body.tag === 'string' && isWholeNumber(body.count)) {
    return { tag: body.tag, count: body.count };
  }

  throw new TypeError('Not a count of a tag.');
}

function readValueCount(body: unknown): ValueCount {
  if (isObject(body) && typeof body.value === 'string' && isWholeNumber(body.count)) {
    return { value: body.value, count: body.count };
  }

  throw new TypeError('Not a count of a value.');
}

function readFieldFacet(body: unknown): FieldFacet {
  if (isObject(body) && typeof body.key === 'string') {
    return { key: body.key, values: readList(body.values, readValueCount) };
  }

  throw new TypeError('Not a facet of a field.');
}

function readRecordsAnswer(body: unknown): RecordsAnswer {
  if (isObject(body) && isObject(body.facets)) {
    const { templates, tags, fields } = body.facets;
    return {
      records: readList(body.records, readListItem),
      tree: readList(body.tree, readStashNode),
      facets: {
        templates: readList(templates, readTemplateCount),
        tags: readList(tags, readTagCount),
        fields: readList(fields, readFieldFacet),
      },
    };
  }

  throw new TypeError('Not an answer of /api/records.');
}

function readStash(body: unknown): Stash {
  if (isObject(body) && typeof body.id === 'string' && typeof body.name === 'string') {
    return { id: body.id, name: body.name };
  }

  throw new TypeError('Not a stash.');
}

function readStashChoices(body: unknown): StashChoice[] {
  if (isObject(body)) {
    return readList(body.stashes, (entry) => {
      if (isObject(entry) && typeof entry.used === 'boolean') {
        return { ...readStash(entry), used: entry.used };
      }

      throw new TypeError('Not a stash of /api/stashes.');
    });
  }

  throw new TypeError('Not an answer of /api/stashes.');
}

function readTags(body: unknown): string[] {
  if (isObject(body)) {
    return readList(body.tags, readText);
  }

  throw new TypeError('Not an answer of /api/tags.');
}

function readTrash(body: unknown): TrashItem[] {
  if (isObject(body)) {
    return readList(body.records, readTrashItem);
  }

  throw new TypeError('Not an answer of /api/trash.');
}

function readVersion(body: unknown): RecordVersion {
  if (isObject(body) && isWholeNumber(body.version) && isDate(body.updatedAt)) {
    return {
      version: body.version,
      updatedBy: readAuthor(body.updatedBy),
      updatedAt: body.updatedAt,
      levels: readList(body.levels, readLevel),
    };
  }

  throw new TypeError('Not a version of a record.');
}

function readHistory(body: unknown): RecordVersion[] {
  if (isObject(body)) {
    return readList(body.versions, readVersion);
  }

  throw new TypeError('Not an answer of the history of a record.');
}

/** The value of a `secret`, which goes into no message: a reader's error is replaced by `invalid`. */
function readSecret(body: unknown): string {
  if (isObject(body) && typeof body.value === 'string') {
    return body.value;
  }

  throw new TypeError('Not an answer of the secret of a record.');
}

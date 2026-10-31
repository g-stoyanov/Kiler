import {
  ApiError,
  messageOf,
  recordsSearch,
  type Author,
  type FieldType,
  type PathStash,
  type RecordData,
  type RecordField,
  type Session,
  type Template,
} from './api.ts';
import { isCodeValue, urlView, valueText } from './fields.ts';

/*
 * The logic of the record view (M6.5, design 8.2): the links of the stash path, who gets which button, what each field
 * shows, and the shown values of the `secret` fields.
 */

/** How long a shown `secret` stays on the screen: long enough to read it or type it by hand (decided in M6.5). */
export const shownSeconds = 30;

/** A stash of the path of a record, with the address of its node in the main view. */
export interface PathPart {
  id: string;
  /** Null when the stash cannot be read; its node is still there. */
  name: string | null;
  to: string;
}

/** The parts of the stash path, each a link to its node: `/?stash=` with the ids up to it (decided at the start of M6). */
export function pathParts(stash: PathStash[]): PathPart[] {
  return stash.map((part, index) => ({
    id: part.id,
    name: part.name,
    to: `/${recordsSearch({ stash: stash.slice(0, index + 1).map((up) => up.id) })}`,
  }));
}

/** Who made a version: the display name, or the account when the team has no user file of it that fits. */
export function authorName(author: Author): string {
  return author.name ?? author.account;
}

/**
 * Whether the session gets "Edit": an editor, while the template of the record can be used. A record whose template is
 * missing or does not fit has `fields` `[]`, and Kiler refuses its edit (docs/api.md, "Record"). The server checks
 * every request anyway.
 */
export function offersEdit(session: Session, record: RecordData): boolean {
  return session.signedIn && session.canEdit && record.fields.length > 0;
}

/**
 * What goes beside a padlock (decided at the start of M6): "Sign in" for a guest, which comes back to the record; a
 * signed-in user below the level has nothing to unlock until the locking per level (PLAN section 10).
 */
export function padlockAction(session: Session): 'signIn' | 'none' {
  return session.signedIn ? 'none' : 'signIn';
}

/** Whether a `secret` gets "Show" and "Copy": the request of a secret refuses a guest (docs/api.md). */
export function offersSecret(session: Session): boolean {
  return session.signedIn;
}

/** A field of the record with what its template says of it. */
export interface FieldRow {
  field: RecordField;
  /** Its label in the template, or its key when the template no longer has it. */
  label: string;
  /** Of the field in the template; undefined without it, so a `url` is text. */
  type: FieldType | undefined;
  /** What the field shows as code, with its language and dialect; null when it is no code. */
  code: { code: string; language: string | null; dialect: string | null } | null;
  /** The text that "Copy" beside the value copies; null for none. */
  copy: string | null;
}

/**
 * The fields of the record in the order of the template, which `fields` has already (docs/api.md, "Record"). A `code`
 * shows as code, with the language of its value or of the template; a `markdown` and a `doc` as text until M7. "Copy"
 * goes beside each value (design 8.2) but a code, whose block has its own (M6.9), a `url` of this computer, which
 * `FieldValue` gives its own, and a `ref` whose record has gone.
 */
export function fieldRows(record: RecordData, template: Template | undefined): FieldRow[] {
  return record.fields.map((field) => {
    const settings = template?.fields.find((entry) => entry.key === field.key);
    const type = settings?.type;
    const row: FieldRow = { field, label: settings?.label ?? field.key, type, code: null, copy: null };
    if (field.kind !== 'value') {
      return row;
    }

    const value = field.value;
    const text = valueText(value);
    if (isCodeValue(value) || (type === 'code' && typeof value === 'string')) {
      const language = isCodeValue(value) ? value.language : (settings?.language ?? null);
      return { ...row, code: { code: text ?? '', language, dialect: settings?.dialect ?? null } };
    }

    if (text === null || (type === 'url' && urlView(text).kind === 'local')) {
      return row;
    }

    return { ...row, copy: text };
  });
}

/**
 * Whether a refusal reads the session again: a 403, as the session may have changed meanwhile (kiler-ui, "API"). The
 * page hides what the session may not do, so a 403 comes only from such a race.
 */
export function readsSession(error: unknown): boolean {
  return error instanceof ApiError && error.kind === 'refused' && error.status === 403;
}

/** What a `secret` shows: `••••`, its request under way, its value, or why the request failed. */
export type SecretView =
  { state: 'hidden' } | { state: 'showing' } | { state: 'shown'; value: string } | { state: 'failed'; message: string };

const hidden: SecretView = { state: 'hidden' };

/**
 * The shown values of the `secret` fields of one record (kiler-ui, "Secret values"). "Show" is one request of the
 * value, and only the last request of a field counts. A shown value hides again after {@link shownSeconds}, on "Hide",
 * and at {@link hideAll}, which the record view calls on leaving the page, at each `session` event and at each read of
 * the record again; it also aborts the requests under way, so no answer shows after it. The value goes nowhere else.
 *
 * For `useSyncExternalStore`: each change is a new snapshot.
 */
export class ShownSecrets {
  readonly #request: (key: string, signal: AbortSignal) => Promise<string>;
  readonly #refused: (error: unknown) => void;
  readonly #listeners = new Set<() => void>();
  readonly #requests = new Map<string, AbortController>();
  readonly #timers = new Map<string, ReturnType<typeof setTimeout>>();
  #views: ReadonlyMap<string, SecretView> = new Map();

  /** `request` gives the value of the field `key`; `refused` hears each failure of it, after it shows. */
  constructor(request: (key: string, signal: AbortSignal) => Promise<string>, refused: (error: unknown) => void) {
    this.#request = request;
    this.#refused = refused;
  }

  readonly subscribe = (listener: () => void): (() => void) => {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  };

  readonly getSnapshot = (): ReadonlyMap<string, SecretView> => this.#views;

  /** What the field `key` shows in `views`, a snapshot. */
  static viewOf(views: ReadonlyMap<string, SecretView>, key: string): SecretView {
    return views.get(key) ?? hidden;
  }

  show(key: string): void {
    this.#forget(key);
    const request = new AbortController();
    this.#requests.set(key, request);
    this.#set(key, { state: 'showing' });
    this.#request(key, request.signal).then(
      (value) => {
        if (!request.signal.aborted) {
          this.#requests.delete(key);
          this.#timers.set(
            key,
            setTimeout(() => this.hide(key), shownSeconds * 1000),
          );
          this.#set(key, { state: 'shown', value });
        }
      },
      (error: unknown) => {
        if (!request.signal.aborted) {
          this.#requests.delete(key);
          this.#set(key, { state: 'failed', message: messageOf(error) });
          this.#refused(error);
        }
      },
    );
  }

  hide(key: string): void {
    this.#forget(key);
    this.#set(key, undefined);
  }

  hideAll(): void {
    for (const request of this.#requests.values()) {
      request.abort();
    }

    for (const timer of this.#timers.values()) {
      clearTimeout(timer);
    }

    this.#requests.clear();
    this.#timers.clear();
    if (this.#views.size > 0) {
      this.#views = new Map();
      this.#notify();
    }
  }

  #forget(key: string): void {
    this.#requests.get(key)?.abort();
    this.#requests.delete(key);
    clearTimeout(this.#timers.get(key));
    this.#timers.delete(key);
  }

  #set(key: string, view: SecretView | undefined): void {
    if (view === undefined && !this.#views.has(key)) {
      return;
    }

    const views = new Map(this.#views);
    if (view === undefined) {
      views.delete(key);
    } else {
      views.set(key, view);
    }

    this.#views = views;
    this.#notify();
  }

  #notify(): void {
    for (const listener of this.#listeners) {
      listener();
    }
  }
}

/**
 * Hides every shown value at each change of the session store (session.ts): a `session` event, which drops the session
 * first, a stop of the stream, and the answer of a sign-in or a lock. `App.tsx` mounts the screens again at these
 * anyway (the key of `AppRoutes`); this keeps the rule in the record view too. Gives the unsubscribe.
 */
export function hideAtSessionChange(store: { subscribe: (listener: () => void) => () => void }, secrets: ShownSecrets) {
  return store.subscribe(() => secrets.hideAll());
}

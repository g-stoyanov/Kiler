import type {
  EditRecordRequest,
  FieldKind,
  LevelName,
  RecordData,
  RecordField,
  RecordRequest,
  RecordsQuery,
  RefValue,
  RequestValue,
  Template,
  TemplateField,
} from './api.ts';
import { isCodeValue, isRefValue, valueText } from './fields.ts';
import { texts } from './texts.ts';

/*
 * The record form (M6.7; design 6.6, "The form"; docs/api.md, `POST /api/records`, `PUT /api/records/{id}` and "Levels
 * of a field"): the draft of a new record or of an edit, generated from the template, its checks, and its request.
 */

/** The key of the field `env`, whose value sets the levels of the fields with `levelByEnv` (design 6.5). */
export const envKey = 'env';

/** The key of the description in `levels` and in the refusals; no field of a template has it (`TemplateField.Header`). */
export const descriptionKey = 'description';

/** The description as a field of the form: a few lines of text at level 0, unless it is raised. */
export const descriptionField: TemplateField = {
  key: descriptionKey,
  label: texts.form.description,
  type: 'multiline',
  level: 0,
  levelByEnv: null,
  required: false,
  filter: false,
  list: false,
  options: null,
  language: null,
  dialect: null,
  languages: null,
  refTemplate: null,
};

/**
 * What the form holds of a field, by its type: the text of an input, the code of a `code` with its language (`''` when
 * the template gives no `languages`), the record of a `ref`, or a `secret`, whose value the page never has. A field that
 * the record has above the editor's levels is `locked`, and one whose group does not decrypt is `unreadable`; neither
 * is sent.
 */
export type DraftValue =
  | { kind: 'text'; text: string }
  | { kind: 'code'; code: string; language: string }
  | { kind: 'ref'; ref: RefValue | null }
  | {
      kind: 'secret';
      /** The new value typed, which the page keeps only until its request is sent (kiler-ui, "Secret values"). */
      typed: string;
      /** The record has a value, which an edit keeps while nothing is typed. */
      kept: boolean;
      /** "Clear": the edit clears the value. */
      clear: boolean;
      /** A value was typed and sent, and the save was refused: it is typed again, as the page did not keep it. */
      again: boolean;
    }
  | { kind: 'locked'; levelName: string | null }
  | { kind: 'unreadable' };

/** How a field of the record started an edit: its level and its kind, for the raise that it keeps. */
export interface StartField {
  level: number;
  kind: FieldKind;
}

/**
 * The record as the editor wants it. A new record has no `start` and chooses its template first; an edit has its
 * template, and starts from the record.
 */
export interface Draft {
  template: Template | null;
  /** The record of an edit: the version that it is over, its `env`, and how each field started, the description too. */
  start: { version: number; env: string | null; fields: Partial<Record<string, StartField>> } | null;
  name: string;
  tags: string[];
  /** By key, the description too. */
  values: Partial<Record<string, DraftValue>>;
  /** The levels that the editor chose, by key; an edit sends only these (docs/api.md, "Levels of a field"). */
  asked: Partial<Record<string, number>>;
}

/** A new record, with its template when one is chosen. */
export function newDraft(template: Template | null): Draft {
  const draft: Draft = { template: null, start: null, name: '', tags: [], values: {}, asked: {} };
  return template === null ? draft : withTemplate(draft, template);
}

/** The template chosen for a new record: its fields empty, and the header as it was. */
export function withTemplate(draft: Draft, template: Template): Draft {
  const values: Partial<Record<string, DraftValue>> = { [descriptionKey]: draft.values[descriptionKey] ?? emptyText };
  for (const field of template.fields) {
    values[field.key] = emptyValue(field);
  }

  return { ...draft, template, values, asked: {} };
}

/**
 * The edit of a record whose template is `template`: the header and every field as the record has them. A value in
 * another shape than the type says, such as a number of a `text`, is shown by its text.
 */
export function editDraft(record: RecordData, template: Template): Draft {
  const fields: Partial<Record<string, StartField>> = {};
  const values: Partial<Record<string, DraftValue>> = {};
  for (const field of [descriptionField, ...template.fields]) {
    const current = field.key === descriptionKey ? record.description : record.fields.find((f) => f.key === field.key);
    if (current !== undefined) {
      fields[field.key] = { level: current.level, kind: current.kind };
    }

    values[field.key] = current === undefined ? emptyValue(field) : startValue(field, current);
  }

  const env = values[envKey];
  return {
    template,
    start: { version: record.version, env: env?.kind === 'text' && env.text !== '' ? env.text : null, fields },
    name: record.name,
    tags: record.tags,
    values,
    asked: {},
  };
}

const emptyText: DraftValue = { kind: 'text', text: '' };

function emptyValue(field: TemplateField): DraftValue {
  switch (field.type) {
    case 'code':
      return { kind: 'code', code: '', language: field.languages?.[0] ?? '' };
    case 'ref':
      return { kind: 'ref', ref: null };
    case 'secret':
      return { kind: 'secret', typed: '', kept: false, clear: false, again: false };
    default:
      return emptyText;
  }
}

function startValue(field: TemplateField, current: RecordField): DraftValue {
  switch (current.kind) {
    case 'locked':
      return { kind: 'locked', levelName: current.levelName };
    case 'unreadable':
      return { kind: 'unreadable' };
    case 'secret':
      return { kind: 'secret', typed: '', kept: true, clear: false, again: false };
    case 'empty':
      return emptyValue(field);
    case 'value':
      break;
  }

  const value = current.value;
  switch (field.type) {
    case 'code': {
      const empty = emptyValue(field);
      const language = empty.kind === 'code' ? empty.language : '';
      return isCodeValue(value)
        ? { kind: 'code', code: value.code, language: field.languages === null ? '' : value.language }
        : { kind: 'code', code: valueText(value) ?? '', language };
    }
    case 'ref':
      return { kind: 'ref', ref: isRefValue(value) ? value : null };
    case 'secret':
      // A value that its group does not mark, of a template changed since: a `secret` now, which the page never has.
      return { kind: 'secret', typed: '', kept: true, clear: false, again: false };
    default:
      return { kind: 'text', text: valueText(value) ?? '' };
  }
}

/** The `env` of the draft, which sets the levels of the fields with `levelByEnv`; null without a value. */
export function envOf(draft: Draft): string | null {
  const env = draft.values[envKey];
  return env?.kind === 'text' && env.text !== '' ? env.text : null;
}

/**
 * The default level of a field (docs/api.md, "Levels of a field"): its `levelByEnv` for `env` when it names it; else its
 * `level`; else the highest of its `levelByEnv`, so an `env` left empty does not lower a password.
 */
export function defaultLevel(field: TemplateField, env: string | null): number {
  const byEnv = field.levelByEnv;
  if (byEnv !== null && env !== null && Object.hasOwn(byEnv, env)) {
    return byEnv[env];
  }

  return field.level ?? Math.max(0, ...Object.values(byEnv ?? {}));
}

/**
 * The level of a field in the draft: the higher of its default under the draft's `env` and the level asked. In an edit,
 * a field that the editor did not change keeps its raise, a level above its default under the `env` that the record had.
 */
export function levelOf(draft: Draft, field: TemplateField): number {
  const base = defaultLevel(field, envOf(draft));
  const asked = draft.asked[field.key];
  if (asked !== undefined) {
    return Math.max(base, asked);
  }

  const start = draft.start?.fields[field.key];
  if (draft.start !== null && start !== undefined) {
    if (start.kind === 'locked') {
      return start.level;
    }

    if (start.kind !== 'empty' && start.level > defaultLevel(field, draft.start.env)) {
      return Math.max(base, start.level);
    }
  }

  return base;
}

/** A field as the form shows it. */
export interface FieldView {
  field: TemplateField;
  value: DraftValue;
  level: number;
  /**
   * The name of the level of a field above the editor's levels, for its padlock; such a field is not sent. Null for a
   * field that the editor writes.
   */
  padlock: string | null;
  /** The levels that the editor may ask: from the default up to the top level of the editor. */
  choices: number[];
}

/**
 * The description and the fields of the template, in its order, each with its level and the levels that the editor may
 * ask, none above its own for `env`. `top` is the top level of the editor. A field is above the editor when the record has it at a level that the
 * editor does not hold, or when it has no value in the record and its level is above `top`, as a value there would be
 * refused; a field with a value that the `env` moves above `top` stays, and Kiler's refusal says why.
 */
export function fieldViews(draft: Draft, top: number, levels: LevelName[]): FieldView[] {
  const fields = draft.template === null ? [] : [descriptionField, ...draft.template.fields];
  return fields.map((field) => {
    const value = draft.values[field.key] ?? emptyValue(field);
    const level = levelOf(draft, field);
    const start = draft.start?.fields[field.key];
    const hasValue = start !== undefined && start.kind !== 'empty';
    let padlock: string | null = null;
    if (value.kind === 'locked') {
      padlock = value.levelName ?? levelName(level, levels);
    } else if (level > top && !hasValue) {
      padlock = levelName(level, levels);
    }

    // `env` stays at level 0, as the name and the path do: a level asked for it is refused.
    const base = defaultLevel(field, envOf(draft));
    const choices = padlock !== null ? [] : field.key === envKey ? [level] : range(base, Math.max(base, top));
    return { field, value, level, padlock, choices };
  });
}

function levelName(level: number, levels: LevelName[]): string {
  return levels.find((entry) => entry.level === level)?.name ?? String(level);
}

function range(from: number, to: number): number[] {
  return Array.from({ length: to - from + 1 }, (_, index) => from + index);
}

/** The problems of the page's checks, or of a refusal with `field`, by key: `template`, `stash`, `name` or a field. */
export type Problems = Partial<Record<string, string>>;

/** The keys in the order of the form, for the focus on the first problem. */
export function problemOrder(draft: Draft): string[] {
  return ['template', 'stash', 'name', descriptionKey, 'tags', ...(draft.template?.fields ?? []).map((f) => f.key)];
}

/**
 * The page's checks before a save: a template, a stash, and no name of a stash typed and left without a choice, as the
 * record would go to the stash before it; a name; a `secret` typed again after a refused save; a value for each
 * required field that the editor writes, and a number for a `number`. Kiler checks the rest, such as a `url`, and its
 * refusal shows at the field.
 */
export function checkDraft(
  draft: Draft,
  stash: { ids: string[]; typed: string },
  top: number,
  levels: LevelName[],
): Problems {
  const problems: Problems = {};
  const t = texts.form.problems;
  if (draft.template === null) {
    problems.template = t.template;
  }

  if (stash.typed.trim() !== '') {
    problems.stash = t.stashTyped;
  } else if (stash.ids.length === 0) {
    problems.stash = t.stash;
  }

  if (draft.name.trim() === '') {
    problems.name = t.name;
  }

  for (const view of fieldViews(draft, top, levels)) {
    if (view.padlock !== null) {
      continue;
    }

    if (view.value.kind === 'secret' && view.value.again && view.value.typed === '' && !view.value.clear) {
      // Sent and refused: the page did not keep it, and a save without it would keep the old value or none.
      problems[view.field.key] = t.again;
      continue;
    }

    const value = requestValue(view);
    if (value === undefined || value === null) {
      if (view.field.required && !(view.value.kind === 'secret' && view.value.kept && !view.value.clear)) {
        problems[view.field.key] = t.required;
      }
    } else if (typeof value === 'number' && !Number.isFinite(value)) {
      problems[view.field.key] = t.number;
    }
  }

  return problems;
}

/**
 * The request of a draft that has passed {@link checkDraft}: the header, each field that the editor writes, and the
 * levels that the editor chose. A field above the editor is left out, and so is a `secret` that an edit keeps; a blank
 * text, an empty code and a `ref` not chosen are no value (`null`).
 */
export function recordRequest(draft: Draft, stash: string[], top: number, levels: LevelName[]): RecordRequest {
  const fields: Record<string, RequestValue> = {};
  const asked: Record<string, number> = {};
  let description: string | null = null;
  for (const view of fieldViews(draft, top, levels)) {
    if (view.padlock !== null) {
      continue;
    }

    const value = requestValue(view);
    if (view.field.key === descriptionKey) {
      description = typeof value === 'string' ? value : null;
    } else if (value !== undefined) {
      fields[view.field.key] = value;
    }

    const level = draft.asked[view.field.key];
    if (level !== undefined) {
      asked[view.field.key] = level;
    }
  }

  return { stash, name: draft.name.trim(), description, tags: draft.tags, fields, levels: asked };
}

/** The value of a field in the request, or undefined to leave it out: a `secret` that an edit keeps, and what is not sent. */
function requestValue({ field, value }: FieldView): RequestValue | undefined {
  switch (value.kind) {
    case 'text': {
      if (value.text.trim() === '') {
        return null;
      }

      switch (field.type) {
        case 'number':
          return Number(value.text.trim());
        case 'url':
        case 'date':
        case 'select':
          return value.text.trim();
        default:
          return value.text;
      }
    }
    case 'code':
      if (value.code === '') {
        return null;
      }

      return field.languages === null ? value.code : { language: value.language, code: value.code };
    case 'ref':
      return value.ref?.id ?? null;
    case 'secret':
      if (value.typed !== '') {
        return value.typed;
      }

      return value.clear ? null : undefined;
    case 'locked':
    case 'unreadable':
      return undefined;
  }
}

/** The state of the form: the draft, the problems shown, and the save under way. */
export interface RecordFormState {
  draft: Draft;
  problems: Problems;
  sending: boolean;
  /** The sentence of a refusal without `field`, or of a request that failed. */
  error: string | undefined;
  /** Grows at each check and refusal, so the screen moves the focus to the first problem. */
  round: number;
  /** The key of the first problem, which takes the focus; undefined for none, as for a refusal without `field`. */
  focus: string | undefined;
  /**
   * The record as it is now, of the 409 of an edit over an older version (docs/api.md, "Conflict"), until "Reload" or
   * "Overwrite" settles it.
   */
  conflict: RecordData | undefined;
}

export type RecordFormAction =
  | { type: 'template'; template: Template }
  | { type: 'name'; text: string }
  | { type: 'tags'; tags: string[] }
  | { type: 'value'; key: string; value: DraftValue }
  | { type: 'level'; key: string; level: number }
  /** A change outside the draft, such as of the stash path, which takes the problem of its key away. */
  | { type: 'touch'; key: string }
  | { type: 'problems'; problems: Problems }
  /** A save under way; `sent` are the keys of the fields in its request. */
  | { type: 'send'; sent: string[] }
  | { type: 'refused'; message: string; field: string | null; current: RecordData | null };

export function newRecordFormState(draft: Draft): RecordFormState {
  return { draft, problems: {}, sending: false, error: undefined, round: 0, focus: undefined, conflict: undefined };
}

export function recordFormReducer(state: RecordFormState, action: RecordFormAction): RecordFormState {
  if (state.sending && action.type !== 'refused') {
    return state;
  }

  const draft = state.draft;
  switch (action.type) {
    case 'template':
      return draft.start === null
        ? { ...state, draft: withTemplate(draft, action.template), problems: {}, error: undefined }
        : state;
    case 'name':
      return edited(state, 'name', { ...draft, name: action.text });
    case 'tags':
      return edited(state, 'tags', { ...draft, tags: action.tags });
    case 'value':
      return edited(state, action.key, { ...draft, values: { ...draft.values, [action.key]: action.value } });
    case 'level':
      return { ...state, draft: { ...draft, asked: { ...draft.asked, [action.key]: action.level } } };
    case 'touch':
      return state.problems[action.key] === undefined ? state : edited(state, action.key, draft);
    case 'problems':
      return {
        ...state,
        problems: action.problems,
        error: undefined,
        round: state.round + 1,
        focus: firstProblem(draft, action.problems),
      };
    case 'send':
      return {
        ...state,
        draft: withoutTyped(draft, action.sent),
        problems: {},
        sending: true,
        error: undefined,
        conflict: undefined,
      };
    case 'refused':
      return state.sending ? afterRefusal(state, action) : state;
  }
}

/** A change of the editor, which takes the problem of that key away. */
function edited(state: RecordFormState, key: string, draft: Draft): RecordFormState {
  const problems = Object.fromEntries(Object.entries(state.problems).filter(([problem]) => problem !== key));
  return { ...state, draft, problems };
}

/**
 * The draft as the save sends it: a value typed into a `secret` is not kept once its request is sent, and one that the
 * request has is asked again after a refusal. One of a field above the editor was not sent, so it is only dropped.
 */
function withoutTyped(draft: Draft, sent: string[]): Draft {
  const values = { ...draft.values };
  for (const [key, value] of Object.entries(values)) {
    if (value?.kind === 'secret' && value.typed !== '') {
      values[key] = { ...value, typed: '', again: sent.includes(key) };
    }
  }

  return { ...draft, values };
}

/**
 * A refused save: a conflict with `current` offers "Reload" and "Overwrite", a refusal with `field` shows at that
 * field, and the others by their sentence. A `secret` that was typed asks to be typed again, as the page did not keep
 * it, also before "Overwrite".
 */
function afterRefusal(
  state: RecordFormState,
  { message, field, current }: Extract<RecordFormAction, { type: 'refused' }>,
): RecordFormState {
  const problems: Problems = {};
  for (const [key, value] of Object.entries(state.draft.values)) {
    if (value?.kind === 'secret' && value.again) {
      problems[key] = texts.form.problems.again;
    }
  }

  if (current !== null) {
    return { ...state, problems, sending: false, round: state.round + 1, focus: conflictKey, conflict: current };
  }

  // A key that the form does not show, which only a template changed meanwhile gives, shows by its sentence.
  const shown = field !== null && problemOrder(state.draft).includes(field);
  if (shown) {
    problems[field] = message;
  }

  return {
    ...state,
    problems,
    sending: false,
    error: shown ? undefined : message,
    round: state.round + 1,
    focus: firstProblem(state.draft, problems),
  };
}

/** The key of the focus at a conflict: its first button. */
export const conflictKey = 'conflict';

/**
 * "Overwrite" of a conflict (M6.8): the edit as the editor has it now, sent again over the version of `current`. The
 * levels are those that the editor chose, so a raise that `current` has stays, as Kiler keeps it.
 */
export function overwriteRequest(
  draft: Draft,
  stash: string[],
  top: number,
  levels: LevelName[],
  current: RecordData,
): EditRecordRequest {
  return { ...recordRequest(draft, stash, top, levels), version: current.version };
}

function firstProblem(draft: Draft, problems: Problems): string | undefined {
  return problemOrder(draft).find((key) => problems[key] !== undefined);
}

/** The tags of a text: separated by commas, each trimmed, the blank ones left out ({@link addTags}). */
export function tagsOf(text: string): string[] {
  return text
    .split(',')
    .map((tag) => tag.trim())
    .filter((tag) => tag !== '');
}

/**
 * The suggestions of the tags (M6.8): the tags of the records whose text has the typed text, without regard to case,
 * none that the record has already, in the order of Kiler.
 */
export function tagSuggestions(known: string[], chosen: string[], typed: string): string[] {
  const text = typed.trim().toLowerCase();
  const taken = new Set(chosen.map((tag) => tag.toLowerCase()));
  return known.filter((tag) => !taken.has(tag.toLowerCase()) && tag.toLowerCase().includes(text));
}

/**
 * The tags with those of `text` added: split at commas and trimmed, the blank ones left out, and each kept once without
 * regard to case, as Kiler keeps them (docs/api.md, `POST /api/records`).
 */
export function addTags(tags: string[], text: string): string[] {
  const result = [...tags];
  for (const tag of tagsOf(text)) {
    if (!result.some((other) => other.toLowerCase() === tag.toLowerCase())) {
      result.push(tag);
    }
  }

  return result;
}

/** The search of the records that a `ref` may point to: those of its `refTemplate` (M5.1, "Left for after the exam"). */
export function refQuery(field: TemplateField, q: string): RecordsQuery {
  return field.refTemplate === null ? { q } : { template: field.refTemplate, q };
}

/**
 * The `env` that the stash path suggests (design 6.6): while the draft's `env` is empty, the option of `env` that is the
 * name of a stash of the path, without regard to case, the deepest stash first; null otherwise. It is only offered:
 * `env` stays the one field that sets the levels, not a free name of a stash.
 */
export function suggestedEnv(draft: Draft, path: { name: string | null }[]): string | null {
  const env = draft.template?.fields.find((field) => field.key === envKey);
  if (env === undefined || envOf(draft) !== null) {
    return null;
  }

  for (const { name } of [...path].reverse()) {
    const option = (env.options ?? []).find((entry) => entry.toLowerCase() === name?.trim().toLowerCase());
    if (option !== undefined) {
      return option;
    }
  }

  return null;
}

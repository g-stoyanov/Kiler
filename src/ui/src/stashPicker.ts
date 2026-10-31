import type { PathStash, Stash, StashChoice } from './api.ts';

/** The positions of a stash path (design 6.6): 1 to 4 stashes, without gaps or repeats. */
export const maxStashes = 4;

/** The characters of an id of a record or a stash (`ItemId` of Kiler.Core): 8 of lowercase Crockford Base32. */
const idPattern = /^[0-9a-hjkmnp-tv-z]{8}$/;

/**
 * The ids of the path of a "+" (`?stash=<ids>` of `/new`): those that can be ids, each once, at most 4, in order. A part
 * that cannot be one ends the path, as the path has no gaps; a stash that is not there is cut when its read comes.
 */
export function pathOfAddress(stash: string | null): string[] {
  const ids: string[] = [];
  for (const id of stash === null || stash === '' ? [] : stash.split(',')) {
    if (!idPattern.test(id) || ids.includes(id) || ids.length === maxStashes) {
      break;
    }

    ids.push(id);
  }

  return ids;
}

/** A stash of the path in the picker; its name is null until the stashes are read. */
export interface PathEntry {
  id: string;
  name: string | null;
}

/** What the picker starts with: the path of a "+", locked until "Change", or the path of a record, which is not. */
export interface StashPickerStart {
  path: PathEntry[];
  locked: boolean;
}

/** The start of a new record from a "+": the ids of its node, whose names come with the first read. */
export function startOfNode(ids: string[]): StashPickerStart {
  return { path: ids.map((id) => ({ id, name: null })), locked: ids.length > 0 };
}

/** The start of an edit: the path of the record. A stash that cannot be read has no name, and its read cuts it. */
export function startOfRecord(stash: PathStash[]): StashPickerStart {
  return { path: stash.map(({ id, name }) => ({ id, name })), locked: false };
}

/**
 * The state of the stash picker (M6.6): the chosen path, how many of its positions a "+" locked, the text typed at the
 * open position, the stashes read for it, and the new stash under way. Only one new stash is sent at a time (M5.4: two
 * at once could make two stashes of one name).
 */
export interface StashPickerState {
  path: PathEntry[];
  /** The positions from the start that are locked, until "Change". */
  locked: number;
  /** The text of the open position. */
  typed: string;
  /** The stashes for the open position, under the path before it. */
  choices: { state: 'reading' } | { state: 'ready'; stashes: StashChoice[] } | { state: 'failed'; message: string };
  /** Grows to read the stashes again for the same path. */
  reads: number;
  /** The name of the new stash under way. */
  creating: string | undefined;
  /** A new stash refused with a 409: the stashes are read again, and the stash of that name is chosen when it is there. */
  pending: { name: string; message: string } | undefined;
  /** Why the last new stash was refused. */
  error: string | undefined;
  /** A stash of the starting path could not be read, so the path was cut before it. */
  cut: boolean;
}

export type StashPickerAction =
  | { type: 'read'; stashes: StashChoice[] }
  | { type: 'readFailed'; message: string }
  | { type: 'readAgain' }
  | { type: 'type'; text: string }
  | { type: 'choose'; stash: Stash }
  | { type: 'clear'; position: number }
  | { type: 'change' }
  | { type: 'create' }
  | { type: 'created'; stash: Stash }
  | { type: 'createRefused'; message: string; status: number | undefined };

export function newStashPickerState({ path, locked }: StashPickerStart): StashPickerState {
  const kept = path.slice(0, maxStashes);
  return {
    path: kept,
    locked: locked ? kept.length : 0,
    typed: '',
    choices: { state: 'reading' },
    reads: 0,
    creating: undefined,
    pending: undefined,
    error: undefined,
    cut: false,
  };
}

/** The ids of the path before the position that the stashes are read for: the open one, or the last one when all 4 are chosen. */
export function readPath(state: StashPickerState): string[] {
  return state.path.slice(0, maxStashes - 1).map((entry) => entry.id);
}

export function stashPickerReducer(state: StashPickerState, action: StashPickerAction): StashPickerState {
  switch (action.type) {
    case 'read':
      return afterRead(state, action.stashes);
    case 'readFailed':
      return {
        ...state,
        choices: { state: 'failed', message: action.message },
        pending: undefined,
        error: state.pending?.message ?? state.error,
      };
    case 'readAgain':
      return { ...state, choices: { state: 'reading' }, reads: state.reads + 1 };
    case 'type':
      return { ...state, typed: action.text, error: undefined };
    case 'choose':
      return state.creating === undefined && state.pending === undefined ? choose(state, action.stash) : state;
    case 'clear':
      return state.creating !== undefined ||
        state.pending !== undefined ||
        action.position < state.locked ||
        action.position >= state.path.length
        ? state
        : {
            ...state,
            path: state.path.slice(0, action.position),
            typed: '',
            choices: { state: 'reading' },
            error: undefined,
            cut: false,
          };
    case 'change':
      return { ...state, locked: 0 };
    case 'create': {
      const name = state.typed.trim();
      return state.creating !== undefined ||
        state.pending !== undefined ||
        state.path.length === maxStashes ||
        name === ''
        ? state
        : { ...state, creating: name, error: undefined };
    }
    case 'created':
      return state.creating === undefined ? state : choose({ ...state, creating: undefined }, action.stash);
    case 'createRefused':
      if (state.creating === undefined) {
        return state;
      }

      // Any 409 of a new stash, as its "already there" shares the status with offline and no data (M5.1): read the
      // stashes again, and choose the one of that name when it is there.
      return action.status === 409
        ? {
            ...state,
            creating: undefined,
            pending: { name: state.creating, message: action.message },
            choices: { state: 'reading' },
            reads: state.reads + 1,
          }
        : { ...state, creating: undefined, error: action.message };
  }
}

/** The stash chosen at the open position, which opens the next one; a stash that the path has is not taken. */
function choose(state: StashPickerState, stash: Stash): StashPickerState {
  if (state.path.length === maxStashes || state.path.some((entry) => entry.id === stash.id)) {
    return state;
  }

  return {
    ...state,
    path: [...state.path, { id: stash.id, name: stash.name }],
    typed: '',
    choices: { state: 'reading' },
    error: undefined,
    cut: false,
  };
}

/**
 * The stashes read for the open position. They also give the names of the starting path: a stash that is not among
 * them cannot be read, so the path ends before it, and the stashes are read again for the shorter path. A new stash
 * refused with a 409 is chosen when a stash of its name is there now.
 */
function afterRead(state: StashPickerState, stashes: StashChoice[]): StashPickerState {
  const missing = state.path.findIndex(
    (entry) => entry.name === null && !stashes.some((stash) => stash.id === entry.id),
  );
  if (missing !== -1) {
    return {
      ...state,
      path: state.path.slice(0, missing),
      locked: Math.min(state.locked, missing),
      choices: { state: 'reading' },
      cut: true,
    };
  }

  const path = state.path.map((entry) =>
    entry.name === null ? { ...entry, name: stashes.find((stash) => stash.id === entry.id)?.name ?? null } : entry,
  );
  const ready: StashPickerState = { ...state, path, choices: { state: 'ready', stashes } };
  if (state.pending === undefined) {
    return ready;
  }

  const { name, message } = state.pending;
  const found = optionsOf(ready).find((option) => option.kind === 'stash' && sameName(option.stash.name, name));
  return found?.kind === 'stash'
    ? choose({ ...ready, pending: undefined }, found.stash)
    : { ...ready, pending: undefined, error: message };
}

/** An option of the open position: a stash, or a new stash of the typed name, which is last. */
export type StashOption = { kind: 'stash'; stash: StashChoice } | { kind: 'create'; name: string };

/**
 * The options of the open position (design 6.6): the stashes whose name has the typed text, without regard to case,
 * those used at that position under that path first, and none that the path has; then "Create …" when the typed text
 * is not blank and no stash has it as its name exactly, without regard to case. None once all 4 are chosen.
 */
export function stashOptions(stashes: StashChoice[], path: PathEntry[], typed: string): StashOption[] {
  if (path.length === maxStashes) {
    return [];
  }

  const text = typed.trim();
  const lower = text.toLowerCase();
  const offered = stashes.filter(
    (stash) => !path.some((entry) => entry.id === stash.id) && stash.name.toLowerCase().includes(lower),
  );
  const options: StashOption[] = [
    ...offered.filter((stash) => stash.used),
    ...offered.filter((stash) => !stash.used),
  ].map((stash) => ({ kind: 'stash', stash }));
  if (text !== '' && !stashes.some((stash) => sameName(stash.name, text))) {
    options.push({ kind: 'create', name: text });
  }

  return options;
}

/** The options of the state's open position, or none while its stashes are not read. */
export function optionsOf(state: StashPickerState): StashOption[] {
  return state.choices.state === 'ready' ? stashOptions(state.choices.stashes, state.path, state.typed) : [];
}

/** Two names of stashes are the same without regard to case, as Kiler compares them (`OrdinalIgnoreCase`). */
function sameName(a: string, b: string): boolean {
  return a.trim().toUpperCase() === b.trim().toUpperCase();
}

/**
 * A position of the path as the picker shows it: `locked` by a "+", `chosen` with its "Clear", `open` for the next
 * stash (the one after the last chosen), and `closed` until the one before it is chosen.
 */
export type PositionView =
  { kind: 'locked'; entry: PathEntry } | { kind: 'chosen'; entry: PathEntry } | { kind: 'open' } | { kind: 'closed' };

export function positionsOf(state: StashPickerState): PositionView[] {
  return Array.from({ length: maxStashes }, (_, position): PositionView => {
    const entry = state.path[position];
    if (entry !== undefined) {
      return position < state.locked ? { kind: 'locked', entry } : { kind: 'chosen', entry };
    }

    return position === state.path.length ? { kind: 'open' } : { kind: 'closed' };
  });
}

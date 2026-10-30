import {
  ApiError,
  levelsChanged,
  type AdminLevel,
  type AdminUserAnswer,
  type AdminUsers,
  type NewUserRequest,
} from './api.ts';
import { texts } from './texts.ts';
import { limits } from './wizard.ts';

/**
 * What an admin screen does after a refused action, beyond showing why (M4.6): `stay` keeps the form, as for a value
 * that does not fit (400); `readList` reads the list again, as the files have changed meanwhile or the action cannot be
 * done now (409); `signInAgain` offers a new sign-in, as `levels.json` has other checks than at the sign-in; and
 * `readSession` reads the session again after any other 403 (kiler-ui, "API").
 */
export type AfterAdminRefusal = 'stay' | 'readList' | 'signInAgain' | 'readSession';

export function afterAdminRefusal(error: unknown): AfterAdminRefusal {
  if (!(error instanceof ApiError) || error.kind !== 'refused') {
    return 'stay';
  }

  switch (error.status) {
    case 403:
      return error.message === levelsChanged ? 'signInAgain' : 'readSession';
    case 409:
      return 'readList';
    default:
      return 'stay';
  }
}

/** "2 · Team": the number and the name of a level, as the screens show it. */
export function levelLabel(level: number, levels: AdminLevel[]): string {
  const name = levels.find((entry) => entry.level === level)?.name;
  return name === undefined ? String(level) : `${String(level)} · ${name}`;
}

/** The top level of `levels.json`. */
export function topLevel(levels: AdminLevel[]): number {
  return levels.length - 1;
}

/** The levels whose keys a temporary password of `level` gives: 1 to `level`, as level 0 has no key. */
export function keysGiven(level: number, levels: AdminLevel[]): AdminLevel[] {
  return levels.filter((entry) => entry.level >= 1 && entry.level <= level);
}

/** The levels that a user may have: 1 to the top level, or only the top level for an admin (M4.6). */
export function levelChoices(levels: AdminLevel[], isAdmin: boolean): AdminLevel[] {
  const top = topLevel(levels);
  return keysGiven(top, levels).filter((entry) => !isAdmin || entry.level === top);
}

/** The fields of a new user as the admin typed them. */
export interface NewUserForm {
  /** The Windows user name, `DOMAIN\` and capitals allowed: Kiler makes the account of it. */
  account: string;
  displayName: string;
  level: number;
  canEdit: boolean;
}

export function newUserForm(): NewUserForm {
  return { account: '', displayName: '', level: 1, canEdit: false };
}

/** Why the fields do not fit, each with a sentence for the page; no member is set when they fit. */
export interface NewUserProblems {
  account?: string;
  displayName?: string;
  level?: string;
}

/**
 * The checks of the page: the account is only asked for, and Kiler says whether it can be one (`AccountName`); the
 * display name is trimmed and has 1 to 100 characters; the level is one of {@link levelChoices}.
 */
export function checkNewUser(form: NewUserForm, levels: AdminLevel[]): NewUserProblems {
  const problems: NewUserProblems = {};
  const t = texts.users.problems;
  if (form.account.trim() === '') {
    problems.account = t.account;
  }

  const displayName = form.displayName.trim();
  if (displayName === '') {
    problems.displayName = t.displayNameBlank;
  } else if (displayName.length > limits.maxDisplayName) {
    problems.displayName = t.displayNameLong;
  }

  if (!levelChoices(levels, false).some((entry) => entry.level === form.level)) {
    problems.level = t.level;
  }

  return problems;
}

export function hasAdminProblems(problems: object): boolean {
  return Object.keys(problems).length > 0;
}

/** The request of a form that has passed {@link checkNewUser}. */
export function newUserRequest(form: NewUserForm): NewUserRequest {
  return {
    account: form.account.trim(),
    displayName: form.displayName.trim(),
    level: form.level,
    canEdit: form.canEdit,
  };
}

/**
 * Why `typed` cannot be the new name of `level`, or undefined when it can: trimmed, 1 to 40 characters, and no other
 * level has it without regard to case (`LevelsFile` of Kiler.Core). Kiler takes a name as given, so the page trims it.
 */
export function checkLevelName(typed: string, level: number, levels: AdminLevel[]): string | undefined {
  const t = texts.levels.problems;
  const name = typed.trim();
  if (name === '') {
    return t.blank;
  }

  if (name.length > limits.maxLevelName) {
    return t.long;
  }

  const folded = name.toUpperCase();
  return levels.some((entry) => entry.level !== level && entry.name.toUpperCase() === folded) ? t.twice : undefined;
}

/** A temporary password that an action gave, with whom it is for; it shows once, until its screen closes. */
export interface TemporaryPassword {
  account: string;
  displayName: string;
  password: string;
  /** The level of the keys that it gives. */
  level: number;
}

/**
 * The users screen: the list with the levels, the panel that is open (a new user, or the actions on one account), the
 * request under way, and the temporary password that the last action gave. The password stays in the state only while
 * its screen is open (kiler-ui, "Secret values"): `closePassword` takes it away, and a new list keeps it.
 */
export interface UsersState {
  list:
    | { state: 'reading' }
    | { state: 'ready'; users: AdminUsers; levels: AdminLevel[] }
    | { state: 'failed'; message: string; signInAgain: boolean };
  panel: { kind: 'none' } | { kind: 'new' } | { kind: 'user'; account: string };
  sending: boolean;
  error: string | undefined;
  signInAgain: boolean;
  /** What the last action did, when it gave no password. */
  done: string | undefined;
  temporary: TemporaryPassword | undefined;
}

export type UsersAction =
  | { type: 'reading' }
  | { type: 'read'; users: AdminUsers; levels: AdminLevel[] }
  | { type: 'readFailed'; message: string; signInAgain: boolean }
  | { type: 'open'; panel: UsersState['panel'] }
  | { type: 'send' }
  | { type: 'refused'; message: string; signInAgain: boolean }
  | { type: 'done'; answer: AdminUserAnswer }
  | { type: 'closePassword' };

export const newUsersState: UsersState = {
  list: { state: 'reading' },
  panel: { kind: 'none' },
  sending: false,
  error: undefined,
  signInAgain: false,
  done: undefined,
  temporary: undefined,
};

export function usersReducer(state: UsersState, action: UsersAction): UsersState {
  switch (action.type) {
    case 'reading':
      // A list that is read again keeps the last one on the screen, and the message of the refusal that asked for it.
      return state.list.state === 'failed' ? { ...state, list: { state: 'reading' } } : state;
    case 'read':
      return { ...state, list: { state: 'ready', users: action.users, levels: action.levels } };
    case 'readFailed':
      return { ...state, list: { state: 'failed', message: action.message, signInAgain: action.signInAgain } };
    case 'open':
      return state.sending
        ? state
        : { ...state, panel: action.panel, error: undefined, signInAgain: false, done: undefined };
    case 'send':
      return state.sending ? state : { ...state, sending: true, error: undefined, signInAgain: false, done: undefined };
    case 'refused':
      return state.sending
        ? { ...state, sending: false, error: action.message, signInAgain: action.signInAgain }
        : state;
    case 'done':
      return state.sending ? afterAction(state, action.answer) : state;
    case 'closePassword':
      return { ...state, temporary: undefined };
  }
}

/**
 * The panel closed, and the password of the action, if any. The screen reads the list again, as the action may have
 * changed the warning of too few admins too.
 */
function afterAction(state: UsersState, { user, temporaryPassword }: AdminUserAnswer): UsersState {
  const name = user.displayName ?? user.account;
  return {
    ...state,
    panel: { kind: 'none' },
    sending: false,
    done: temporaryPassword === null ? texts.users.saved(name) : undefined,
    temporary:
      temporaryPassword === null
        ? undefined
        : { account: user.account, displayName: name, password: temporaryPassword, level: user.level ?? 0 },
  };
}

import type { WizardRequest } from './api.ts';
import { texts } from './texts.ts';

/**
 * The limits of the wizard, as Kiler checks them (M4.1, M4.3): `LevelsFile.MaxTop` and `MaxNameLength`,
 * `UserFile.MaxDisplayNameLength`, `Password.MinLength` and `MaxLength`, `SettingsFile.MaxAutoLockMinutes`.
 */
export const limits = {
  minLevels: 2,
  maxLevels: 10,
  maxLevelName: 40,
  maxDisplayName: 100,
  minPassword: 8,
  maxPassword: 1024,
  maxAutoLockMinutes: 1440,
} as const;

/** The fields of the wizard as the user typed them. */
export interface WizardForm {
  /** The names of the levels 0 to the top level. */
  levels: string[];
  displayName: string;
  password: string;
  repeat: string;
  guestMode: boolean;
  autoLockMinutes: string;
}

/** Why the fields do not fit, each with a sentence for the page; no member is set when they fit. */
export interface WizardProblems {
  levels?: string;
  /** By the index of the level. */
  levelNames?: Record<number, string>;
  displayName?: string;
  password?: string;
  repeat?: string;
  autoLockMinutes?: string;
}

/** The made-up levels of the design and the demo data; the admin renames them. */
export function newWizardForm(): WizardForm {
  return {
    levels: ['Guest', 'Team', 'Test', 'Prod'],
    displayName: '',
    password: '',
    repeat: '',
    guestMode: false,
    autoLockMinutes: '30',
  };
}

/**
 * The checks of the page, the same as Kiler's, so the user sees each problem at its field before a request. The names
 * are trimmed: Kiler takes a name as given, so "Team " would be a name of its own. A password is taken as typed.
 */
export function checkWizard(form: WizardForm): WizardProblems {
  const problems: WizardProblems = {};
  const t = texts.wizard.problems;

  if (form.levels.length < limits.minLevels || form.levels.length > limits.maxLevels) {
    problems.levels = t.levelCount;
  }

  const levelNames: Record<number, string> = {};
  const seen = new Set<string>();
  form.levels.forEach((typed, level) => {
    const name = typed.trim();
    const folded = name.toUpperCase();
    if (name === '') {
      levelNames[level] = t.levelBlank;
    } else if (name.length > limits.maxLevelName) {
      levelNames[level] = t.levelLong;
    } else if (seen.has(folded)) {
      levelNames[level] = t.levelTwice;
    }

    seen.add(folded);
  });
  if (Object.keys(levelNames).length > 0) {
    problems.levelNames = levelNames;
  }

  const displayName = form.displayName.trim();
  if (displayName === '') {
    problems.displayName = t.displayNameBlank;
  } else if (displayName.length > limits.maxDisplayName) {
    problems.displayName = t.displayNameLong;
  }

  if (form.password.length < limits.minPassword || form.password.length > limits.maxPassword) {
    problems.password = t.password;
  } else if (form.repeat !== form.password) {
    problems.repeat = t.repeat;
  }

  if (readMinutes(form.autoLockMinutes) === undefined) {
    problems.autoLockMinutes = t.autoLockMinutes;
  }

  return problems;
}

export function hasProblems(problems: WizardProblems): boolean {
  return Object.keys(problems).length > 0;
}

/** The request of a form that has passed {@link checkWizard}. */
export function wizardRequest(form: WizardForm): WizardRequest {
  const autoLockMinutes = readMinutes(form.autoLockMinutes);
  if (autoLockMinutes === undefined) {
    throw new Error('The form has not passed its checks.');
  }

  return {
    levels: form.levels.map((name) => name.trim()),
    displayName: form.displayName.trim(),
    password: form.password,
    guestMode: form.guestMode,
    autoLockMinutes,
  };
}

/** Whole minutes from 1 to the limit, as typed; undefined for anything else. */
function readMinutes(typed: string): number | undefined {
  const text = typed.trim();
  if (!/^\d{1,4}$/.test(text)) {
    return undefined;
  }

  const minutes = Number(text);
  return minutes >= 1 && minutes <= limits.maxAutoLockMinutes ? minutes : undefined;
}

/**
 * The steps of the wizard: the form, which keeps its password only until its request is sent; then the recovery key,
 * only until the user says it is saved; then done, with nothing of either (kiler-ui, "Secret values").
 */
export type WizardState =
  | { step: 'form'; form: WizardForm; sending: boolean; error: string | undefined }
  | { step: 'key'; recoveryKey: string; saved: boolean }
  | { step: 'done' };

export type WizardAction =
  | { type: 'edit'; form: WizardForm }
  | { type: 'send' }
  | { type: 'refused'; message: string }
  | { type: 'made'; recoveryKey: string }
  | { type: 'saved'; saved: boolean }
  | { type: 'confirm' };

export function newWizardState(): WizardState {
  return { step: 'form', form: newWizardForm(), sending: false, error: undefined };
}

export function wizardReducer(state: WizardState, action: WizardAction): WizardState {
  switch (action.type) {
    case 'edit':
      return state.step === 'form' && !state.sending ? { ...state, form: action.form } : state;
    case 'send':
      // The request has the password by now; the form lets it go, and a refusal asks for it again.
      return state.step === 'form' && !state.sending
        ? { step: 'form', form: { ...state.form, password: '', repeat: '' }, sending: true, error: undefined }
        : state;
    case 'refused':
      // Only the form shows a refusal: a second request that Kiler refused must not replace the recovery key.
      return state.step === 'form' ? { ...state, sending: false, error: action.message } : state;
    case 'made':
      return state.step === 'form' ? { step: 'key', recoveryKey: action.recoveryKey, saved: false } : state;
    case 'saved':
      return state.step === 'key' ? { ...state, saved: action.saved } : state;
    case 'confirm':
      return state.step === 'key' && state.saved ? { step: 'done' } : state;
  }
}

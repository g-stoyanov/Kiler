import { ApiError, passwordRefusals, type PasswordRequest } from './api.ts';
import { texts } from './texts.ts';
import { limits } from './wizard.ts';

/** The fields of the change of password as the user typed them; the forced change has no current password. */
export interface PasswordForm {
  current: string;
  password: string;
  repeat: string;
}

/** Why the fields do not fit, each with a sentence for the page; no member is set when they fit. */
export interface PasswordProblems {
  current?: string;
  password?: string;
  repeat?: string;
}

/**
 * The checks of the page, the same as Kiler's for the new password (`Password.Invalid`), so the user sees each problem
 * at its field before a request. The current password is only asked for: Kiler checks it, a wrong one with a 403 and
 * one of a length that no password has with the 400 of the length (under "Left for after the exam" in M4.md).
 */
export function checkPasswordForm(form: PasswordForm, forced: boolean): PasswordProblems {
  const problems: PasswordProblems = {};
  const t = texts.password.problems;
  if (!forced && form.current === '') {
    problems.current = t.current;
  }

  if (form.password.length < limits.minPassword || form.password.length > limits.maxPassword) {
    problems.password = t.password;
  } else if (form.repeat !== form.password) {
    problems.repeat = t.repeat;
  }

  return problems;
}

export function hasPasswordProblems(problems: PasswordProblems): boolean {
  return Object.keys(problems).length > 0;
}

/** The request of a form that has passed {@link checkPasswordForm}. */
export function passwordRequest(form: PasswordForm, forced: boolean): PasswordRequest {
  return { current: forced ? null : form.current, password: form.password };
}

/**
 * What the page does after a refused change, beyond showing why: `signInAgain` offers a new sign-in, as the user file
 * has changed since the sign-in (M4.5a); `readSession` reads the session again, as for a 403 of any endpoint (kiler-ui,
 * "API"), except the two 403s about the password itself, which keep the form. A 400 of the forced change is one too:
 * the page has checked the new password, so Kiler asks for the current one, as another page has changed the temporary
 * password already, and a change of password gives no `session` event.
 */
export type AfterRefusal = 'stay' | 'signInAgain' | 'readSession';

export function afterPasswordRefusal(error: unknown, forced: boolean): AfterRefusal {
  if (!(error instanceof ApiError) || error.kind !== 'refused') {
    return 'stay';
  }

  switch (error.status) {
    case 400:
      return forced ? 'readSession' : 'stay';
    case 403:
      return error.message === passwordRefusals.wrongPassword || error.message === passwordRefusals.levelNotUnlocked
        ? 'stay'
        : 'readSession';
    case 409:
      return error.message === passwordRefusals.userChanged ? 'signInAgain' : 'stay';
    default:
      return 'stay';
  }
}

/**
 * The form, which keeps its passwords only until its request is sent (kiler-ui, "Secret values"). `changed` shows that
 * the last request changed the password; `signInAgain` that Kiler asks for a new sign-in.
 */
export interface PasswordState {
  form: PasswordForm;
  sending: boolean;
  error: string | undefined;
  signInAgain: boolean;
  changed: boolean;
}

export type PasswordAction =
  | { type: 'edit'; form: PasswordForm }
  | { type: 'send' }
  | { type: 'refused'; message: string; signInAgain: boolean }
  | { type: 'changed' };

const emptyForm: PasswordForm = { current: '', password: '', repeat: '' };

export const newPasswordState: PasswordState = {
  form: emptyForm,
  sending: false,
  error: undefined,
  signInAgain: false,
  changed: false,
};

export function passwordReducer(state: PasswordState, action: PasswordAction): PasswordState {
  switch (action.type) {
    case 'edit':
      return state.sending ? state : { ...state, form: action.form, changed: false };
    case 'send':
      // The request has the passwords by now; the form lets them go, and a refusal asks for them again.
      return state.sending ? state : { ...newPasswordState, sending: true };
    case 'refused':
      return state.sending
        ? { ...state, sending: false, error: action.message, signInAgain: action.signInAgain }
        : state;
    case 'changed':
      return state.sending ? { ...newPasswordState, changed: true } : state;
  }
}

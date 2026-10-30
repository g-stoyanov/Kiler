import { describe, expect, it } from 'vitest';
import { ApiError, passwordRefusals } from './api.ts';
import {
  afterPasswordRefusal,
  checkPasswordForm,
  hasPasswordProblems,
  newPasswordState,
  passwordReducer,
  passwordRequest,
  type PasswordForm,
  type PasswordState,
} from './changePassword.ts';
import { texts } from './texts.ts';

const problems = texts.password.problems;

function form(changes: Partial<PasswordForm> = {}): PasswordForm {
  return { current: 'old-password', password: 'new-password', repeat: 'new-password', ...changes };
}

function refused(status: number, message: string): ApiError {
  return new ApiError('refused', message, status);
}

describe('checkPasswordForm', () => {
  it('finds no problem in a form that fits', () => {
    expect(checkPasswordForm(form(), false)).toEqual({});
    expect(hasPasswordProblems(checkPasswordForm(form(), false))).toBe(false);
  });

  it('asks for the current password in a voluntary change, and not in the forced one', () => {
    expect(checkPasswordForm(form({ current: '' }), false)).toEqual({ current: problems.current });
    expect(checkPasswordForm(form({ current: '' }), true)).toEqual({});
  });

  it('leaves the length of the current password to Kiler, which says whether it is right', () => {
    expect(checkPasswordForm(form({ current: 'x' }), false)).toEqual({});
  });

  it.each([
    ['7 characters', 'x'.repeat(7)],
    ['1025 characters', 'x'.repeat(1025)],
  ])('refuses a new password of %s', (_, password) => {
    expect(checkPasswordForm(form({ password, repeat: password }), true)).toEqual({ password: problems.password });
  });

  it('takes a new password of 8 and of 1024 characters, as typed, spaces too', () => {
    for (const password of [' '.repeat(8), 'x'.repeat(1024)]) {
      expect(checkPasswordForm(form({ password, repeat: password }), true)).toEqual({});
    }
  });

  it('refuses two new passwords that differ', () => {
    expect(checkPasswordForm(form({ repeat: 'new-passwort' }), true)).toEqual({ repeat: problems.repeat });
  });
});

describe('passwordRequest', () => {
  it('sends the current password in a voluntary change, and null in the forced one', () => {
    expect(passwordRequest(form(), false)).toEqual({ current: 'old-password', password: 'new-password' });
    expect(passwordRequest(form(), true)).toEqual({ current: null, password: 'new-password' });
  });
});

describe('afterPasswordRefusal', () => {
  it.each([
    ['a wrong current password', refused(403, passwordRefusals.wrongPassword)],
    ['a level that did not unlock', refused(403, passwordRefusals.levelNotUnlocked)],
  ])('keeps the form after %s', (_, error) => {
    expect(afterPasswordRefusal(error, false)).toBe('stay');
  });

  it.each([
    ['no session', refused(403, 'Sign in first.')],
    ['a lock while the request ran', refused(403, 'The session has been locked. Sign in again.')],
  ])('reads the session again after the 403 of %s', (_, error) => {
    expect(afterPasswordRefusal(error, false)).toBe('readSession');
    expect(afterPasswordRefusal(error, true)).toBe('readSession');
  });

  it('offers a new sign-in when the user file has changed since the sign-in', () => {
    expect(afterPasswordRefusal(refused(409, passwordRefusals.userChanged), false)).toBe('signInAgain');
    expect(afterPasswordRefusal(refused(409, passwordRefusals.userChanged), true)).toBe('signInAgain');
  });

  it.each([
    ['being written', 'Your user file is being written now. Try again in a moment.'],
    ['offline', 'The shared folder cannot be reached. The password can be changed once it is back.'],
  ])('keeps the form after the 409 of a user file %s', (_, message) => {
    expect(afterPasswordRefusal(refused(409, message), true)).toBe('stay');
  });

  it('reads the session again after a 400 of the forced change, as another page has changed the password', () => {
    const error = refused(400, 'Give your current password.');

    expect(afterPasswordRefusal(error, true)).toBe('readSession');
    expect(afterPasswordRefusal(error, false)).toBe('stay');
  });

  it.each([
    ['a lost cookie', new ApiError('unauthorized', texts.errors.unauthorized, 401)],
    ['no answer', new ApiError('network', texts.errors.network)],
    ['an error of the page', new Error('broken')],
  ])('keeps the form after %s', (_, error) => {
    expect(afterPasswordRefusal(error, true)).toBe('stay');
  });
});

describe('passwordReducer', () => {
  function typed(): PasswordState {
    return passwordReducer(newPasswordState, { type: 'edit', form: form() });
  }

  function sending(): PasswordState {
    return passwordReducer(typed(), { type: 'send' });
  }

  it('lets every password go once its request is sent', () => {
    expect(sending()).toEqual({ ...newPasswordState, sending: true });
    expect(sending().form).toEqual({ current: '', password: '', repeat: '' });
  });

  it("shows Kiler's sentence of a refusal, and asks for the passwords again", () => {
    const state = passwordReducer(sending(), {
      type: 'refused',
      message: passwordRefusals.userChanged,
      signInAgain: true,
    });

    expect(state).toEqual({
      ...newPasswordState,
      error: passwordRefusals.userChanged,
      signInAgain: true,
    });
  });

  it('says that the password has been changed, with no password left, until the user types again', () => {
    const changed = passwordReducer(sending(), { type: 'changed' });
    expect(changed).toEqual({ ...newPasswordState, changed: true });

    expect(passwordReducer(changed, { type: 'edit', form: form() }).changed).toBe(false);
  });

  it('drops the refusal and the offer of a new sign-in with the next request', () => {
    const refusedState = passwordReducer(sending(), { type: 'refused', message: 'x', signInAgain: true });
    const again = passwordReducer(passwordReducer(refusedState, { type: 'edit', form: form() }), { type: 'send' });

    expect(again.error).toBeUndefined();
    expect(again.signInAgain).toBe(false);
  });

  it('takes no typing, no second request and no answer out of turn', () => {
    const running = sending();
    expect(passwordReducer(running, { type: 'edit', form: form() })).toBe(running);
    expect(passwordReducer(running, { type: 'send' })).toBe(running);

    const idle = typed();
    expect(passwordReducer(idle, { type: 'changed' })).toBe(idle);
    expect(passwordReducer(idle, { type: 'refused', message: 'x', signInAgain: false })).toBe(idle);
  });
});

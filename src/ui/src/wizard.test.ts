import { describe, expect, it } from 'vitest';
import {
  checkWizard,
  hasProblems,
  newWizardForm,
  newWizardState,
  wizardReducer,
  wizardRequest,
  type WizardForm,
  type WizardState,
} from './wizard.ts';
import { texts } from './texts.ts';

const problems = texts.wizard.problems;

function form(changes: Partial<WizardForm> = {}): WizardForm {
  return {
    ...newWizardForm(),
    displayName: 'Demo Admin',
    password: 'demo-password',
    repeat: 'demo-password',
    ...changes,
  };
}

describe('checkWizard', () => {
  it('finds no problem in a form that fits', () => {
    expect(checkWizard(form())).toEqual({});
    expect(hasProblems(checkWizard(form()))).toBe(false);
  });

  it('starts with the made-up levels and the lock after 30 minutes, without the admin', () => {
    const fresh = newWizardForm();

    expect(fresh.levels).toEqual(['Guest', 'Team', 'Test', 'Prod']);
    expect(fresh.autoLockMinutes).toBe('30');
    expect(checkWizard(fresh)).toEqual({ displayName: problems.displayNameBlank, password: problems.password });
  });

  it.each([
    ['one level', ['Guest']],
    ['eleven levels', Array.from({ length: 11 }, (_, level) => `Level ${level}`)],
  ])('refuses %s', (_, levels) => {
    expect(checkWizard(form({ levels })).levels).toBe(problems.levelCount);
  });

  it('takes ten levels', () => {
    expect(checkWizard(form({ levels: Array.from({ length: 10 }, (_, level) => `Level ${level}`) }))).toEqual({});
  });

  it('marks each level whose name is blank, too long, or that of a level below it without regard to case', () => {
    const found = checkWizard(form({ levels: ['Guest', '  ', 'x'.repeat(41), 'guest', 'Prod'] }));

    expect(found.levelNames).toEqual({ 1: problems.levelBlank, 2: problems.levelLong, 3: problems.levelTwice });
  });

  it('trims a name before it checks it, so the same name with a space is the same name', () => {
    expect(checkWizard(form({ levels: ['Guest', 'Team', 'Team '] })).levelNames).toEqual({ 2: problems.levelTwice });
    expect(checkWizard(form({ levels: ['Guest', ` ${'x'.repeat(40)} `] }))).toEqual({});
  });

  it.each([
    ['blank', '   ', problems.displayNameBlank],
    ['too long', 'x'.repeat(101), problems.displayNameLong],
  ])('refuses a display name that is %s', (_, displayName, problem) => {
    expect(checkWizard(form({ displayName })).displayName).toBe(problem);
  });

  it.each([
    ['7 characters', 'x'.repeat(7)],
    ['1025 characters', 'x'.repeat(1025)],
  ])('refuses a password of %s', (_, password) => {
    expect(checkWizard(form({ password, repeat: password })).password).toBe(problems.password);
  });

  it('takes a password of 8 and of 1024 characters, as typed, spaces too', () => {
    for (const password of [' '.repeat(8), 'x'.repeat(1024)]) {
      expect(checkWizard(form({ password, repeat: password }))).toEqual({});
    }
  });

  it('refuses two passwords that differ', () => {
    expect(checkWizard(form({ repeat: 'demo-passwort' })).repeat).toBe(problems.repeat);
  });

  it.each(['', '0', '1441', '1.5', '-5', 'ten', '1e3'])('refuses %j minutes', (autoLockMinutes) => {
    expect(checkWizard(form({ autoLockMinutes })).autoLockMinutes).toBe(problems.autoLockMinutes);
  });

  it.each(['1', '1440', ' 15 '])('takes %j minutes', (autoLockMinutes) => {
    expect(checkWizard(form({ autoLockMinutes }))).toEqual({});
  });
});

describe('wizardRequest', () => {
  it('sends the names trimmed, the password as typed, and the minutes as a number', () => {
    const request = wizardRequest(
      form({ levels: [' Guest', 'Team '], displayName: ' Demo Admin ', password: ' pass word ', guestMode: true }),
    );

    expect(request).toEqual({
      levels: ['Guest', 'Team'],
      displayName: 'Demo Admin',
      password: ' pass word ',
      guestMode: true,
      autoLockMinutes: 30,
    });
  });
});

describe('wizardReducer', () => {
  function sent(): WizardState {
    const typed = wizardReducer(newWizardState(), { type: 'edit', form: form() });
    return wizardReducer(typed, { type: 'send' });
  }

  function shown(): WizardState {
    return wizardReducer(sent(), { type: 'made', recoveryKey: 'ABCD-EFGH-JKMN-PQRS-TVWX-YZ01-2345-6789' });
  }

  it('lets the password go once its request is sent', () => {
    const state = sent();

    expect(state).toMatchObject({ step: 'form', sending: true });
    expect(state.step === 'form' && [state.form.password, state.form.repeat]).toEqual(['', '']);
    expect(JSON.stringify(state)).not.toContain('demo-password');
  });

  it('keeps the rest of the form after a refusal, which it shows', () => {
    const state = wizardReducer(sent(), { type: 'refused', message: 'The shared folder cannot be reached.' });

    expect(state).toMatchObject({
      step: 'form',
      sending: false,
      error: 'The shared folder cannot be reached.',
      form: { displayName: 'Demo Admin', password: '' },
    });
  });

  it('takes no edit and no second send while the request runs', () => {
    const state = sent();

    expect(wizardReducer(state, { type: 'edit', form: form({ displayName: 'Other' }) })).toBe(state);
    expect(wizardReducer(state, { type: 'send' })).toBe(state);
  });

  it('shows the recovery key, and nothing of the form', () => {
    const state = shown();

    expect(state).toEqual({ step: 'key', recoveryKey: 'ABCD-EFGH-JKMN-PQRS-TVWX-YZ01-2345-6789', saved: false });
  });

  it('keeps the recovery key when a second request is refused', () => {
    const state = shown();

    expect(wizardReducer(state, { type: 'refused', message: 'Another Kiler has just been made.' })).toBe(state);
    expect(wizardReducer(state, { type: 'made', recoveryKey: 'OTHER' })).toBe(state);
  });

  it('keeps the recovery key until the user has said it is saved', () => {
    const state = shown();

    expect(wizardReducer(state, { type: 'confirm' })).toBe(state);
  });

  it('lets the recovery key go once it is confirmed', () => {
    const saved = wizardReducer(shown(), { type: 'saved', saved: true });

    const state = wizardReducer(saved, { type: 'confirm' });

    expect(state).toEqual({ step: 'done' });
    expect(JSON.stringify(state)).not.toContain('ABCD');
  });
});

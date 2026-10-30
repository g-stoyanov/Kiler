import { describe, expect, it } from 'vitest';
import {
  afterAdminRefusal,
  checkLevelName,
  checkNewUser,
  hasAdminProblems,
  keysGiven,
  levelChoices,
  levelLabel,
  newUserForm,
  newUserRequest,
  newUsersState,
  usersReducer,
  type NewUserForm,
  type UsersState,
} from './admin.ts';
import { ApiError, levelsChanged, type AdminLevel, type AdminUser, type AdminUsers } from './api.ts';
import { texts } from './texts.ts';

const levels: AdminLevel[] = [
  { level: 0, name: 'Guest' },
  { level: 1, name: 'Team' },
  { level: 2, name: 'Test' },
  { level: 3, name: 'Prod' },
];

const user: AdminUser = {
  account: 'demo.user',
  displayName: 'Demo User',
  level: 2,
  canEdit: false,
  isAdmin: false,
  mustChangePassword: true,
  keysMatchLevel: true,
  invalid: null,
};

const users: AdminUsers = { users: [user], fewAdmins: true };

function form(changes: Partial<NewUserForm> = {}): NewUserForm {
  return { account: 'DEMO\\Demo.User', displayName: 'Demo User', level: 2, canEdit: true, ...changes };
}

function refused(status: number, message: string): ApiError {
  return new ApiError('refused', message, status);
}

describe('afterAdminRefusal', () => {
  it.each([
    ['a value that does not fit', refused(400, 'level is not from 1 to 3.'), 'stay'],
    ['levels.json with other checks', refused(403, levelsChanged), 'signInAgain'],
    ['no right', refused(403, 'Only an admin who holds the keys of every level can do this.'), 'readSession'],
    [
      'a file that changed meanwhile',
      refused(409, 'The file has changed meanwhile. Read it again and try once more.'),
      'readList',
    ],
    ['a status without a sentence', new ApiError('status', 'HTTP 500', 500), 'stay'],
    ['no answer', new ApiError('network', 'Kiler does not answer.'), 'stay'],
    ['an error that is not from a request', new Error('internal'), 'stay'],
  ])('after %s: %s', (_, error, after) => {
    expect(afterAdminRefusal(error)).toBe(after);
  });
});

describe('the levels', () => {
  it('names a level by its number and name', () => {
    expect(levelLabel(2, levels)).toBe('2 · Test');
    expect(levelLabel(7, levels)).toBe('7');
  });

  it('gives the keys of the levels 1 to the level, never of level 0', () => {
    expect(keysGiven(2, levels).map((entry) => entry.level)).toEqual([1, 2]);
    expect(keysGiven(0, levels)).toEqual([]);
  });

  it('offers a user the levels 1 to the top level, and an admin only the top level', () => {
    expect(levelChoices(levels, false).map((entry) => entry.level)).toEqual([1, 2, 3]);
    expect(levelChoices(levels, true).map((entry) => entry.level)).toEqual([3]);
  });
});

describe('checkNewUser', () => {
  const problems = texts.users.problems;

  it('finds no problem in a form that fits, and leaves the form of the account to Kiler', () => {
    expect(checkNewUser(form(), levels)).toEqual({});
    expect(hasAdminProblems(checkNewUser(form(), levels))).toBe(false);
  });

  it('asks for the account and the display name', () => {
    expect(checkNewUser(form({ account: '  ', displayName: ' ' }), levels)).toEqual({
      account: problems.account,
      displayName: problems.displayNameBlank,
    });
  });

  it('takes a display name of 100 characters after the trim, and refuses one of 101', () => {
    expect(checkNewUser(form({ displayName: ` ${'x'.repeat(100)} ` }), levels)).toEqual({});
    expect(checkNewUser(form({ displayName: 'x'.repeat(101) }), levels)).toEqual({
      displayName: problems.displayNameLong,
    });
  });

  it.each([0, 4])('refuses level %i, as a user has a level from 1 to the top level', (level) => {
    expect(checkNewUser(form({ level }), levels)).toEqual({ level: problems.level });
  });

  it('starts with an empty form at level 1', () => {
    expect(newUserForm()).toEqual({ account: '', displayName: '', level: 1, canEdit: false });
  });

  it('sends the account and the display name trimmed', () => {
    expect(newUserRequest(form({ account: ' DEMO\\Demo.User ', displayName: ' Demo User ' }))).toEqual({
      account: 'DEMO\\Demo.User',
      displayName: 'Demo User',
      level: 2,
      canEdit: true,
    });
  });
});

describe('checkLevelName', () => {
  const problems = texts.levels.problems;

  it('takes a new name, and the name of the level itself in another case', () => {
    expect(checkLevelName('Staging', 2, levels)).toBeUndefined();
    expect(checkLevelName('TEST', 2, levels)).toBeUndefined();
  });

  it('trims the name before its checks', () => {
    expect(checkLevelName('   ', 2, levels)).toBe(problems.blank);
    expect(checkLevelName(` ${'x'.repeat(40)} `, 2, levels)).toBeUndefined();
    expect(checkLevelName('x'.repeat(41), 2, levels)).toBe(problems.long);
  });

  it('refuses the name of another level without regard to case', () => {
    expect(checkLevelName(' prod ', 2, levels)).toBe(problems.twice);
    expect(checkLevelName('guest', 2, levels)).toBe(problems.twice);
  });
});

describe('usersReducer', () => {
  const ready: UsersState = { ...newUsersState, list: { state: 'ready', users, levels } };

  function sent(state: UsersState = ready): UsersState {
    return usersReducer(usersReducer(state, { type: 'open', panel: { kind: 'user', account: user.account } }), {
      type: 'send',
    });
  }

  function withPassword(): UsersState {
    return usersReducer(sent(), { type: 'done', answer: { user, temporaryPassword: 'a1b2c3d4e5f6g7h8' } });
  }

  it('shows the temporary password of an action, with whom it is for, and closes the panel', () => {
    const state = withPassword();

    expect(state.temporary).toEqual({
      account: 'demo.user',
      displayName: 'Demo User',
      password: 'a1b2c3d4e5f6g7h8',
      level: 2,
    });
    expect(state.panel).toEqual({ kind: 'none' });
    expect(state.sending).toBe(false);
  });

  it('takes the temporary password away when its screen closes', () => {
    const state = usersReducer(withPassword(), { type: 'closePassword' });

    expect(state.temporary).toBeUndefined();
    expect(JSON.stringify(state)).not.toContain('a1b2c3d4e5f6g7h8');
  });

  it('keeps the temporary password while the list is read again', () => {
    let state = usersReducer(withPassword(), { type: 'reading' });
    state = usersReducer(state, { type: 'read', users: { users: [user], fewAdmins: false }, levels });

    expect(state.temporary?.password).toBe('a1b2c3d4e5f6g7h8');
    expect(state.list).toMatchObject({ state: 'ready', users: { fewAdmins: false } });
  });

  it('says what an action without a password did', () => {
    const state = usersReducer(sent(), { type: 'done', answer: { user, temporaryPassword: null } });

    expect(state.temporary).toBeUndefined();
    expect(state.done).toBe(texts.users.saved('Demo User'));
  });

  it('keeps the panel open with the sentence of a refusal, and asks for a new sign-in when Kiler does', () => {
    const state = usersReducer(sent(), { type: 'refused', message: levelsChanged, signInAgain: true });

    expect(state).toMatchObject({
      panel: { kind: 'user', account: 'demo.user' },
      sending: false,
      error: levelsChanged,
      signInAgain: true,
    });
  });

  it('takes no answer while nothing is sent, and opens no panel while a request runs', () => {
    expect(usersReducer(ready, { type: 'done', answer: { user, temporaryPassword: 'a1b2c3d4e5f6g7h8' } })).toBe(ready);
    expect(usersReducer(ready, { type: 'refused', message: 'no', signInAgain: false })).toBe(ready);

    const state = sent();
    expect(usersReducer(state, { type: 'open', panel: { kind: 'new' } })).toBe(state);
  });

  it('keeps the last list on the screen while it is read again, and reads a failed one from the start', () => {
    expect(usersReducer(ready, { type: 'reading' })).toBe(ready);

    const failed = usersReducer(ready, { type: 'readFailed', message: 'no', signInAgain: false });
    expect(usersReducer(failed, { type: 'reading' }).list).toEqual({ state: 'reading' });
  });
});

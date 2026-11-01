import { describe, expect, it } from 'vitest';
import { ApiError, levelsChanged, type AdminStash } from './api.ts';
import {
  afterStashRefusal,
  checkStashName,
  mergeChoices,
  newStashesState,
  offersDelete,
  panelStash,
  stashesReducer,
  type StashesState,
} from './stashes.ts';
import { texts } from './texts.ts';

const payments: AdminStash = { id: 'a1b2c3d4', name: 'Payments', version: 3, records: 30 };
const prod: AdminStash = { id: 'e5f6g7h8', name: 'Prod', version: 1, records: 12 };
const reports: AdminStash = { id: 'j9k0m1n2', name: 'Reports', version: 2, records: 0 };
const stashes = [payments, prod, reports];

describe('the choices of a merge', () => {
  it('are the other live stashes, without the stash itself, in the order of the list', () => {
    expect(mergeChoices(stashes, prod.id)).toEqual([payments, reports]);
  });

  it('are none when the stash is the only one', () => {
    expect(mergeChoices([payments], payments.id)).toEqual([]);
  });
});

describe('"Delete"', () => {
  it.each([
    [0, true],
    [1, false],
    [30, false],
  ])('with %i shared records is offered: %s', (records, offered) => {
    expect(offersDelete({ ...reports, records })).toBe(offered);
  });
});

describe('the stash of the panel', () => {
  it('is none without a panel', () => {
    expect(panelStash(stashes, { kind: 'none' })).toBeUndefined();
  });

  it.each(['rename', 'merge'] as const)('of a %s is the stash at the version it was opened with', (kind) => {
    expect(panelStash(stashes, { kind, id: payments.id, version: 3 })).toBe(payments);
  });

  it('is gone when another admin has changed the stash, or it has been merged or deleted', () => {
    expect(panelStash(stashes, { kind: 'rename', id: payments.id, version: 2 })).toBeUndefined();
    expect(panelStash([prod, reports], { kind: 'merge', id: payments.id, version: 3 })).toBeUndefined();
  });

  it('of a delete is there only while no shared record is in the stash', () => {
    expect(panelStash(stashes, { kind: 'delete', id: reports.id, version: 2 })).toBe(reports);
    expect(panelStash([{ ...reports, records: 1 }], { kind: 'delete', id: reports.id, version: 2 })).toBeUndefined();
  });
});

describe('the check of a new name', () => {
  const t = texts.stashes.problems;

  it.each([
    ['a blank name', '   ', t.blank],
    ['the name of another stash in another case', ' prod ', t.twice],
    ['its own name in another case', 'PAYMENTS', undefined],
    ['a new name, trimmed', ' Payments 2026 ', undefined],
  ])('gives %s %j', (_, typed, problem) => {
    expect(checkStashName(typed, payments.id, stashes)).toBe(problem);
  });
});

describe('after a refused action', () => {
  const refused = (status: number, message = 'Kiler says why.') => new ApiError('refused', message, status);

  it.each([
    ['a stash merged or deleted meanwhile (404)', 'readList', refused(404)],
    ['a stash changed meanwhile, or not now (409)', 'readList', refused(409)],
    ['a name that does not fit (400)', 'stay', refused(400)],
    ['levels.json changed since the sign-in (403)', 'signInAgain', refused(403, levelsChanged)],
    ['another 403', 'readSession', refused(403)],
    ['a 404 without a sentence', 'stay', new ApiError('status', 'Kiler answered with an error (HTTP 404).', 404)],
    ['no answer', 'stay', new ApiError('network', 'Kiler does not answer.')],
  ])('%s goes on with %s', (_, after, error) => {
    expect(afterStashRefusal(error)).toBe(after);
  });
});

describe('the state of the screen', () => {
  const ready: StashesState = {
    ...newStashesState,
    list: { state: 'ready', stashes },
    panel: { kind: 'rename', id: payments.id, version: 3 },
  };
  const sending = stashesReducer(ready, { type: 'send' });

  it('shows the list of the answer after an action, with what it did, and closes the panel', () => {
    const done = stashesReducer(sending, { type: 'done', stashes: [prod, reports], message: 'Done.' });

    expect(done).toEqual({
      ...newStashesState,
      list: { state: 'ready', stashes: [prod, reports] },
      done: 'Done.',
    });
  });

  it('keeps the panel after a refusal, with its sentence', () => {
    const refused = stashesReducer(sending, { type: 'refused', message: 'Taken.', signInAgain: false });

    expect(refused).toEqual({ ...ready, refusal: { message: 'Taken.', signInAgain: false } });
    expect(stashesReducer(refused, { type: 'send' }).refusal).toBeUndefined();
  });

  it('opens no other panel and sends nothing more while a request runs', () => {
    expect(stashesReducer(sending, { type: 'open', panel: { kind: 'none' } })).toBe(sending);
    expect(stashesReducer(sending, { type: 'send' })).toBe(sending);
    expect(stashesReducer(ready, { type: 'done', stashes: [], message: 'Done.' })).toBe(ready);
  });

  it('keeps the last list on the screen while it reads it again, but not a failed read', () => {
    const failed = stashesReducer(ready, { type: 'readFailed', message: 'Not now.', signInAgain: false });

    expect(stashesReducer(ready, { type: 'reading' })).toBe(ready);
    expect(stashesReducer(failed, { type: 'reading' }).list).toEqual({ state: 'reading' });
  });
});

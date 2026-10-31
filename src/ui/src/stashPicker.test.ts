import { describe, expect, it } from 'vitest';
import type { StashChoice } from './api.ts';
import {
  newStashPickerState,
  optionsOf,
  pathOfAddress,
  positionsOf,
  readPath,
  startOfNode,
  startOfRecord,
  stashOptions,
  stashPickerReducer,
  type StashPickerAction,
  type StashPickerState,
} from './stashPicker.ts';

const alpha: StashChoice = { id: 'a1b2c3d4', name: 'Alpha', used: false };
const beta: StashChoice = { id: 'e5f6g7h8', name: 'Beta', used: false };
const payments: StashChoice = { id: 'j9k0m1n2', name: 'Payments', used: false };
const prod: StashChoice = { id: 'p3q4r5s6', name: 'Prod', used: false };
const testing: StashChoice = { id: 't7v8w9x0', name: 'Test', used: false };
const cyrillic: StashChoice = { id: 'y1z2a3b4', name: 'Плащания', used: false };

const all = [alpha, beta, payments, prod, testing, cyrillic];

function run(state: StashPickerState, ...actions: StashPickerAction[]): StashPickerState {
  return actions.reduce(stashPickerReducer, state);
}

const empty = newStashPickerState(startOfNode([]));

describe('pathOfAddress', () => {
  it('takes the ids of the node in order', () => {
    expect(pathOfAddress('a1b2c3d4,e5f6g7h8')).toEqual(['a1b2c3d4', 'e5f6g7h8']);
  });

  it.each([
    ['no stash', null, []],
    ['an empty stash', '', []],
    ['a part that cannot be an id', 'a1b2c3d4,NOT-AN-ID,e5f6g7h8', ['a1b2c3d4']],
    ['a letter that Crockford leaves out', 'a1b2c3d4,e5f6g7hu', ['a1b2c3d4']],
    ['an empty part', ',a1b2c3d4', []],
    ['a repeat', 'a1b2c3d4,e5f6g7h8,a1b2c3d4', ['a1b2c3d4', 'e5f6g7h8']],
    ['more than 4', 'a1b2c3d4,e5f6g7h8,j9k0m1n2,p3q4r5s6,t7v8w9x0', ['a1b2c3d4', 'e5f6g7h8', 'j9k0m1n2', 'p3q4r5s6']],
  ])('ends the path at %s', (_, stash, expected) => {
    expect(pathOfAddress(stash)).toEqual(expected);
  });
});

describe('stashOptions', () => {
  it('gives the used stashes first, then the rest, each in the order of Kiler', () => {
    const options = stashOptions([beta, { ...prod, used: true }, alpha, { ...testing, used: true }], [], '');

    expect(options).toEqual([
      { kind: 'stash', stash: { ...prod, used: true } },
      { kind: 'stash', stash: { ...testing, used: true } },
      { kind: 'stash', stash: beta },
      { kind: 'stash', stash: alpha },
    ]);
  });

  it('filters by the typed text, without regard to case, in Latin and in Cyrillic', () => {
    expect(stashOptions(all, [], ' A ').map(nameOf)).toEqual(['Alpha', 'Beta', 'Payments', 'Create “A”']);
    expect(stashOptions(all, [], 'ПЛАЩ').map(nameOf)).toEqual(['Плащания', 'Create “ПЛАЩ”']);
  });

  it('offers "Create …" last only when no stash has the typed name exactly, without regard to case', () => {
    expect(stashOptions(all, [], 'Reports').map(nameOf)).toEqual(['Create “Reports”']);
    expect(stashOptions(all, [], '  prod ').map(nameOf)).toEqual(['Prod']);
    expect(stashOptions(all, [], 'pro').map(nameOf)).toEqual(['Prod', 'Create “pro”']);
  });

  it('offers no "Create …" for a blank text', () => {
    expect(stashOptions([], [], '   ')).toEqual([]);
  });

  it('leaves out a stash of the path, and offers no new stash of its name', () => {
    const path = [{ id: alpha.id, name: alpha.name }];

    expect(stashOptions(all, path, '').map(nameOf)).not.toContain('Alpha');
    expect(stashOptions(all, path, 'alpha')).toEqual([]);
  });

  it('offers nothing once all 4 positions are chosen', () => {
    const path = [alpha, payments, prod, testing].map(({ id, name }) => ({ id, name }));

    expect(stashOptions(all, path, 'new')).toEqual([]);
  });
});

describe('stashPickerReducer', () => {
  it('opens only the first position at the start', () => {
    expect(positionsOf(empty).map((view) => view.kind)).toEqual(['open', 'closed', 'closed', 'closed']);
  });

  it('opens a position once the one before it is chosen, and reads the stashes under the new path', () => {
    const state = run(empty, { type: 'read', stashes: all }, { type: 'choose', stash: alpha });

    expect(positionsOf(state).map((view) => view.kind)).toEqual(['chosen', 'open', 'closed', 'closed']);
    expect(state.choices).toEqual({ state: 'reading' });
    expect(readPath(state)).toEqual([alpha.id]);
  });

  it('clears the text typed when a stash is chosen', () => {
    const state = run(
      empty,
      { type: 'read', stashes: all },
      { type: 'type', text: 'al' },
      { type: 'choose', stash: alpha },
    );

    expect(state.typed).toBe('');
  });

  it('does not take a stash that the path has', () => {
    const state = run(empty, { type: 'choose', stash: alpha }, { type: 'choose', stash: alpha });

    expect(state.path).toEqual([{ id: alpha.id, name: 'Alpha' }]);
  });

  it('clears a position and those after it', () => {
    const chosen = run(
      empty,
      { type: 'choose', stash: alpha },
      { type: 'choose', stash: payments },
      { type: 'choose', stash: prod },
      { type: 'type', text: 'te' },
    );

    const state = run(chosen, { type: 'clear', position: 1 });

    expect(state.path).toEqual([{ id: alpha.id, name: 'Alpha' }]);
    expect(state.typed).toBe('');
    expect(positionsOf(state).map((view) => view.kind)).toEqual(['chosen', 'open', 'closed', 'closed']);
  });

  it('ends at position 4, which has no next', () => {
    const state = run(
      empty,
      { type: 'choose', stash: alpha },
      { type: 'choose', stash: payments },
      { type: 'choose', stash: prod },
      { type: 'choose', stash: testing },
      { type: 'read', stashes: all },
      { type: 'type', text: 'Beta' },
    );

    expect(positionsOf(state).map((view) => view.kind)).toEqual(['chosen', 'chosen', 'chosen', 'chosen']);
    expect(readPath(state)).toEqual([alpha.id, payments.id, prod.id]);
    expect(optionsOf(state)).toEqual([]);
    expect(run(state, { type: 'choose', stash: beta }).path).toHaveLength(4);
    expect(run(state, { type: 'create' }).creating).toBeUndefined();
  });

  describe('from a "+"', () => {
    const start = newStashPickerState(startOfNode([alpha.id, payments.id]));

    it('locks the positions of the node, and opens the next one', () => {
      expect(positionsOf(start).map((view) => view.kind)).toEqual(['locked', 'locked', 'open', 'closed']);
      expect(readPath(start)).toEqual([alpha.id, payments.id]);
    });

    it('takes the names of the node from the read', () => {
      const state = run(start, { type: 'read', stashes: all });

      expect(state.path).toEqual([
        { id: alpha.id, name: 'Alpha' },
        { id: payments.id, name: 'Payments' },
      ]);
      expect(state.choices).toEqual({ state: 'ready', stashes: all });
    });

    it('does not clear a locked position', () => {
      expect(run(start, { type: 'clear', position: 0 }).path).toHaveLength(2);
    });

    it('unlocks the positions at "Change", which can then be cleared', () => {
      const state = run(start, { type: 'change' }, { type: 'clear', position: 1 });

      expect(state.path).toEqual([{ id: alpha.id, name: null }]);
      expect(positionsOf(state).map((view) => view.kind)).toEqual(['chosen', 'open', 'closed', 'closed']);
    });

    it('locks all 4 positions of a node at position 4, with no position open', () => {
      const state = newStashPickerState(startOfNode([alpha.id, payments.id, prod.id, testing.id]));

      expect(positionsOf(state).map((view) => view.kind)).toEqual(['locked', 'locked', 'locked', 'locked']);
    });

    it('cuts the path before a stash that the read does not have, and reads again for the shorter path', () => {
      const state = run(start, { type: 'read', stashes: [alpha, prod] });

      expect(state.path).toEqual([{ id: alpha.id, name: null }]);
      expect(state.locked).toBe(1);
      expect(state.cut).toBe(true);
      expect(state.choices).toEqual({ state: 'reading' });
      expect(run(state, { type: 'read', stashes: [alpha, prod] }).path).toEqual([{ id: alpha.id, name: 'Alpha' }]);
    });
  });

  it('keeps the path of a record unlocked, and its names', () => {
    const state = newStashPickerState(startOfRecord([{ id: alpha.id, name: 'Alpha' }]));

    expect(positionsOf(state).map((view) => view.kind)).toEqual(['chosen', 'open', 'closed', 'closed']);
    expect(run(state, { type: 'read', stashes: all }).path).toEqual([{ id: alpha.id, name: 'Alpha' }]);
  });

  describe('a new stash', () => {
    const typed = run(empty, { type: 'read', stashes: all }, { type: 'type', text: '  Reports ' });
    const reportsStash = { id: 'c5d6e7f8', name: 'Reports' };

    it('sends the trimmed name, and chooses the stash that Kiler made', () => {
      const creating = run(typed, { type: 'create' });
      const state = run(creating, { type: 'created', stash: reportsStash });

      expect(creating.creating).toBe('Reports');
      expect(state.creating).toBeUndefined();
      expect(state.path).toEqual([reportsStash]);
    });

    it('sends one at a time', () => {
      const creating = run(typed, { type: 'create' }, { type: 'type', text: 'Other' }, { type: 'create' });

      expect(creating.creating).toBe('Reports');
      expect(run(creating, { type: 'choose', stash: alpha }).path).toEqual([]);
    });

    it('is not made of a blank name', () => {
      expect(run(empty, { type: 'type', text: '  ' }, { type: 'create' }).creating).toBeUndefined();
    });

    it('shows the sentence of a refusal other than a 409', () => {
      const state = run(typed, { type: 'create' }, { type: 'createRefused', message: 'No right.', status: 403 });

      expect(state.creating).toBeUndefined();
      expect(state.error).toBe('No right.');
      expect(state.reads).toBe(typed.reads);
    });

    describe('after a 409', () => {
      const refused = run(
        typed,
        { type: 'create' },
        { type: 'createRefused', message: 'There is a stash of that name already.', status: 409 },
      );

      it('reads the stashes again', () => {
        expect(refused.pending).toEqual({ name: 'Reports', message: 'There is a stash of that name already.' });
        expect(refused.reads).toBe(typed.reads + 1);
        expect(refused.choices).toEqual({ state: 'reading' });
      });

      it('chooses the stash of that name when it is there, without regard to case', () => {
        const state = run(refused, {
          type: 'read',
          stashes: [...all, { id: 'c5d6e7f8', name: 'REPORTS', used: false }],
        });

        expect(state.pending).toBeUndefined();
        expect(state.error).toBeUndefined();
        expect(state.path).toEqual([{ id: 'c5d6e7f8', name: 'REPORTS' }]);
      });

      it('shows the sentence when no stash of that name is there, as for offline', () => {
        const state = run(refused, { type: 'read', stashes: all });

        expect(state.pending).toBeUndefined();
        expect(state.path).toEqual([]);
        expect(state.error).toBe('There is a stash of that name already.');
      });

      it('shows the sentence when the read fails', () => {
        const state = run(refused, { type: 'readFailed', message: 'Kiler does not answer.' });

        expect(state.error).toBe('There is a stash of that name already.');
        expect(state.choices).toEqual({ state: 'failed', message: 'Kiler does not answer.' });
      });

      it('takes no choice or clear until the read', () => {
        expect(run(refused, { type: 'choose', stash: alpha }).path).toEqual([]);
      });
    });
  });
});

function nameOf(option: ReturnType<typeof stashOptions>[number]): string {
  return option.kind === 'stash' ? option.stash.name : `Create “${option.name}”`;
}

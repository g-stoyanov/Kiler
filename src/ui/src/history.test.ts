import { describe, expect, it } from 'vitest';
import type { RecordVersion, Session } from './api.ts';
import { holdsEvery, offersDelete, offersTrashRestore, offersVersionRestore, pathText } from './history.ts';

const guest: Session = {
  account: 'demo.user',
  signedIn: false,
  displayName: null,
  levels: [0],
  canEdit: false,
  isAdmin: false,
  mustChangePassword: false,
  guestMode: true,
};

const reader: Session = { ...guest, signedIn: true, displayName: 'Demo Reader', levels: [0, 1] };
const editor: Session = { ...reader, displayName: 'Demo Editor', levels: [0, 1, 2], canEdit: true };
const admin: Session = { ...editor, displayName: 'Demo Admin', levels: [0, 1, 2, 3], isAdmin: true };

function version(number: number, levels: number[]): RecordVersion {
  return {
    version: number,
    updatedBy: { account: 'demo.editor', name: 'Demo Editor' },
    updatedAt: '2026-10-21T11:32:00Z',
    levels,
  };
}

describe('holdsEvery', () => {
  it('holds the levels that the session reads, and no levels at all', () => {
    expect(holdsEvery(editor, [1, 2])).toBe(true);
    expect(holdsEvery(editor, [])).toBe(true);
    expect(holdsEvery(editor, [1, 3])).toBe(false);
  });
});

describe('"Delete" and "Restore" from the trash', () => {
  it.each([
    ['an editor who holds every level of the record', editor, [1, 2], true],
    ['an editor of a record with no groups', editor, [], true],
    ['an admin, who holds every level', admin, [1, 3], true],
    ['an editor below a level of the record', editor, [1, 3], false],
    ['a user without the right to edit', reader, [1], false],
    ['a guest', guest, [], false],
    ['a session that is not signed in, whatever its rights', { ...guest, canEdit: true }, [], false],
  ])('is offered to %s: %s', (_name, session, levels, offered) => {
    expect(offersDelete(session, { levels })).toBe(offered);
    expect(offersTrashRestore(session, { levels })).toBe(offered);
  });
});

describe('"Restore" of an old version', () => {
  const versions = [version(8, [1, 2]), version(7, [1, 3]), version(6, [1])];

  it('is not offered for the current version', () => {
    expect(offersVersionRestore(admin, versions, versions[0] ?? version(0, []))).toBe(false);
  });

  it('is offered for an old version whose levels and those of the current one the editor holds', () => {
    expect(offersVersionRestore(editor, versions, version(6, [1]))).toBe(true);
    expect(offersVersionRestore(admin, versions, version(7, [1, 3]))).toBe(true);
  });

  it('is not offered when the old version has a level that the editor does not hold', () => {
    expect(offersVersionRestore(editor, versions, version(7, [1, 3]))).toBe(false);
  });

  it('is not offered when the current version has a level that the editor does not hold', () => {
    const higher = [version(8, [1, 3]), version(6, [1])];
    expect(offersVersionRestore(editor, higher, version(6, [1]))).toBe(false);
  });

  it('is not offered to a user without the right to edit, or a guest', () => {
    expect(offersVersionRestore(reader, versions, version(6, [1]))).toBe(false);
    expect(offersVersionRestore(guest, [version(2, []), version(1, [])], version(1, []))).toBe(false);
  });
});

describe('pathText', () => {
  it('joins the names of the path, and names a stash that cannot be read', () => {
    expect(
      pathText([
        { id: 'a1b2c3d4', name: 'Payments' },
        { id: 'e5f6g7h8', name: null },
      ]),
    ).toBe('Payments › A stash that cannot be read');
  });
});

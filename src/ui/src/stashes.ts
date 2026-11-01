import { ApiError, type AdminStash } from './api.ts';
import { afterAdminRefusal, type AfterAdminRefusal } from './admin.ts';
import { texts } from './texts.ts';

/**
 * The panel of the stash screen: an action on one stash, with the version of the list that it was opened from, which its
 * request sends, so a stash that another admin has changed meanwhile is refused (409) instead of changed again unseen.
 */
export type StashPanel = { kind: 'none' } | { kind: 'rename' | 'merge' | 'delete'; id: string; version: number };

/** The stash screen: the list, the panel that is open, the request under way, and what the last action did or why not. */
export interface StashesState {
  list:
    | { state: 'reading' }
    | { state: 'ready'; stashes: AdminStash[] }
    | { state: 'failed'; message: string; signInAgain: boolean };
  panel: StashPanel;
  sending: boolean;
  refusal: { message: string; signInAgain: boolean } | undefined;
  done: string | undefined;
}

export type StashesAction =
  | { type: 'reading' }
  | { type: 'read'; stashes: AdminStash[] }
  | { type: 'readFailed'; message: string; signInAgain: boolean }
  | { type: 'open'; panel: StashPanel }
  | { type: 'send' }
  | { type: 'refused'; message: string; signInAgain: boolean }
  | { type: 'done'; stashes: AdminStash[]; message: string };

export const newStashesState: StashesState = {
  list: { state: 'reading' },
  panel: { kind: 'none' },
  sending: false,
  refusal: undefined,
  done: undefined,
};

export function stashesReducer(state: StashesState, action: StashesAction): StashesState {
  switch (action.type) {
    case 'reading':
      // A list that is read again keeps the last one on the screen, and the refusal that asked for it.
      return state.list.state === 'failed' ? { ...state, list: { state: 'reading' } } : state;
    case 'read':
      return { ...state, list: { state: 'ready', stashes: action.stashes } };
    case 'readFailed':
      return { ...state, list: { state: 'failed', message: action.message, signInAgain: action.signInAgain } };
    case 'open':
      return state.sending ? state : { ...state, panel: action.panel, refusal: undefined, done: undefined };
    case 'send':
      return state.sending ? state : { ...state, sending: true, refusal: undefined, done: undefined };
    case 'refused':
      // The panel stays, so a name that is taken can be changed; it goes once the list no longer has its stash as it was.
      return state.sending
        ? { ...state, sending: false, refusal: { message: action.message, signInAgain: action.signInAgain } }
        : state;
    case 'done':
      // The answer is the list after the action.
      return state.sending
        ? {
            ...state,
            list: { state: 'ready', stashes: action.stashes },
            panel: { kind: 'none' },
            sending: false,
            done: action.message,
          }
        : state;
  }
}

/**
 * The stash of the panel, while the list has it at the version that the panel was opened with; a delete only while no
 * shared record is in it. A list read again after a change of another admin, or after a refusal, so closes the panel.
 */
export function panelStash(stashes: AdminStash[], panel: StashPanel): AdminStash | undefined {
  if (panel.kind === 'none') {
    return undefined;
  }

  const stash = stashes.find((entry) => entry.id === panel.id && entry.version === panel.version);
  return stash !== undefined && (panel.kind !== 'delete' || offersDelete(stash)) ? stash : undefined;
}

/** The stashes that `id` can be merged into: every other live stash of the list, by name as Kiler gives them. */
export function mergeChoices(stashes: AdminStash[], id: string): AdminStash[] {
  return stashes.filter((entry) => entry.id !== id);
}

/** "Delete" only for a stash that no shared record is in (design 6.6); Kiler checks it again. */
export function offersDelete(stash: AdminStash): boolean {
  return stash.records === 0;
}

/**
 * Why `typed` cannot be the new name of the stash `id`, or undefined when it can: trimmed, not blank, and no other live
 * stash has it without regard to case. Kiler checks both again, and also a name too long for a document.
 */
export function checkStashName(typed: string, id: string, stashes: AdminStash[]): string | undefined {
  const t = texts.stashes.problems;
  const name = typed.trim();
  if (name === '') {
    return t.blank;
  }

  const folded = name.toUpperCase();
  return stashes.some((entry) => entry.id !== id && entry.name.toUpperCase() === folded) ? t.twice : undefined;
}

/**
 * What the screen does after a refused action: as on the other admin screens ({@link afterAdminRefusal}), and a 404, a
 * stash merged or deleted meanwhile, reads the list again too (docs/api.md, `PUT /api/admin/stashes/{id}`).
 */
export function afterStashRefusal(error: unknown): AfterAdminRefusal {
  return error instanceof ApiError && error.kind === 'refused' && error.status === 404
    ? 'readList'
    : afterAdminRefusal(error);
}

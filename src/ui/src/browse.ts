import { recordsSearch, type PathStash, type RecordsQuery, type StashNode } from './api.ts';
import { texts } from './texts.ts';

/*
 * The logic of the main view (M6.3): the query in the address and back, the search field, the state of the stash tree
 * and the shortening of a long value in the list.
 */

const fieldPrefix = 'field.';

/**
 * The query of the main view from the search part of its address, the reverse of `recordsSearch` of `api.ts` (decided at
 * the start of M6): `stash` split at its commas, `tag` once per tag, and the keys that start with `field.`. A blank part
 * is left out, and a part given more than once, but `tag`, counts once, by its first.
 */
export function queryFromSearch(search: string): RecordsQuery {
  const params = new URLSearchParams(search);
  const query: RecordsQuery = {};
  const q = params.get('q');
  if (q !== null && q.trim() !== '') {
    query.q = q;
  }

  const stash = (params.get('stash') ?? '').split(',').filter((id) => id !== '');
  if (stash.length > 0) {
    query.stash = stash;
  }

  const template = params.get('template');
  if (template !== null && template.trim() !== '') {
    query.template = template;
  }

  const tags = params.getAll('tag').filter((tag) => tag.trim() !== '');
  if (tags.length > 0) {
    query.tags = tags;
  }

  // A key is any text of the address, `__proto__` too, so the fields go into a map first: `Object.fromEntries` makes
  // each an own member, where an assignment to `__proto__` would be lost.
  const fields = new Map<string, string>();
  for (const [name, value] of params) {
    const key = name.slice(fieldPrefix.length);
    if (name.startsWith(fieldPrefix) && key !== '' && value.trim() !== '' && !fields.has(key)) {
      fields.set(key, value);
    }
  }

  if (fields.size > 0) {
    query.fields = Object.fromEntries(fields);
  }

  return query;
}

/** The address of the main view with a query. */
export function browsePath(query: RecordsQuery): string {
  return `/${recordsSearch(query)}`;
}

/** The address of a new record, at the node of `path` when given (the "+" of the tree, design 6.6). */
export function newRecordPath(path?: readonly string[]): string {
  return `/new${recordsSearch({ stash: path === undefined ? undefined : [...path] })}`;
}

/**
 * The search and the filters of a query, without its node, as one text: `''` when there are none. While it is not
 * blank, the tree shows only the branches with results, expanded (design 6.6), and it names that search.
 */
export function filterKey(query: RecordsQuery): string {
  return recordsSearch({ ...query, stash: undefined });
}

/** The address of the page as the search field reads it: React Router's `key` of each entry, and its `state`. */
export interface Entry {
  key: string;
  state: unknown;
}

/**
 * What the search field has typed. It holds on the entry of the address where the typing began (`at`), and on those
 * that it wrote itself, which carry its `id` in their state ({@link draftState}); any other entry, such as one of Back
 * or of a link, shows the search of its address again. So a key typed while the address changes is not lost.
 */
export interface SearchDraft {
  text: string;
  id: string;
  at: string;
}

/**
 * The id of a new draft. The state of an entry stays over a reload and in a duplicated tab, so the id is one that no
 * other load of the page gives, or a draft would hold on an entry of an earlier load (M6.3 code review).
 */
export function newDraftId(): string {
  return crypto.randomUUID();
}

/** The state of an address that the search field writes, by which its draft knows its own. */
export function draftState(draft: SearchDraft): { searchDraft: string } {
  return { searchDraft: draft.id };
}

function draftHolds(draft: SearchDraft | null, entry: Entry): draft is SearchDraft {
  if (draft === null) {
    return false;
  }

  const state = entry.state;
  const written = typeof state === 'object' && state !== null && 'searchDraft' in state ? state.searchDraft : undefined;
  return entry.key === draft.at || written === draft.id;
}

/** A key of the search field: the draft goes on while it holds, and else a new one, `id`, begins at this entry. */
export function typeSearch(draft: SearchDraft | null, entry: Entry, text: string, id: string): SearchDraft {
  return draftHolds(draft, entry) ? { ...draft, text } : { text, id, at: entry.key };
}

/** The text of the search field: the draft while it holds, and else the search of the address. */
export function searchText(draft: SearchDraft | null, entry: Entry, addressText: string): string {
  return draftHolds(draft, entry) ? draft.text : addressText;
}

/** The wait after the last key before the search is read (design 11). */
export const searchWait = 150;

/** The draft whose text the address does not have yet, which goes there after `searchWait`; null for none. */
export function pendingSearch(draft: SearchDraft | null, entry: Entry, addressText: string): SearchDraft | null {
  return draftHolds(draft, entry) && draft.text !== addressText ? draft : null;
}

/**
 * Ctrl+K, which focuses the search from anywhere on the page (design 8.1), and ⌘K. By the key's place on the keyboard,
 * so it works with the Cyrillic layout too.
 */
export function isSearchShortcut(event: {
  code: string;
  ctrlKey: boolean;
  metaKey: boolean;
  altKey: boolean;
  shiftKey: boolean;
}): boolean {
  return event.code === 'KeyK' && (event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey;
}

/** A node of the tree by the ids of its path: the same stash can sit at several places (design 6.6). */
export function nodeKey(path: readonly string[]): string {
  return path.join(',');
}

/** The place of a node in the tree: the ids of its path, which its link and its "+" take, its key, and its names. */
export interface NodePlace {
  path: string[];
  key: string;
  names: string[];
}

/** The place above the nodes of level 1, without a path. */
export const treeTop: NodePlace = { path: [], key: '', names: [] };

/** The place of a node under the place of the node above it. */
export function nodePlace(parent: NodePlace, node: StashNode): NodePlace {
  const path = [...parent.path, node.id];
  return { path, key: nodeKey(path), names: [...parent.names, stashName(node)] };
}

/**
 * What the tree shows open (design 6.6). `open` are the nodes that the user opened, kept in `localStorage`; the tree
 * starts with none, so collapsed to level 1. During a search or a filter every branch with results is open, but those
 * that the user closed in it (`closed`, for the search `filter`); once it is cleared, `open` shows again.
 */
export interface TreeState {
  open: readonly string[];
  filter: string;
  closed: readonly string[];
}

export function treeState(open: readonly string[]): TreeState {
  return { open, filter: '', closed: [] };
}

/** Whether a node shows open, with `filter` the search and filters of the address (`filterKey`). */
export function isNodeOpen(tree: TreeState, key: string, filter: string): boolean {
  if (filter === '') {
    return tree.open.includes(key);
  }

  return !(tree.filter === filter && tree.closed.includes(key));
}

/** Opens or closes a node: outside a search in `open`, and during one only for that search. */
export function toggleNode(tree: TreeState, key: string, filter: string): TreeState {
  if (filter === '') {
    return { ...tree, open: toggled(tree.open, key) };
  }

  return { ...tree, filter, closed: toggled(tree.filter === filter ? tree.closed : [], key) };
}

/** Opens a chosen node and those above it, so it shows also from an address, such as a link of a record's path. */
export function revealNode(tree: TreeState, path: readonly string[]): TreeState {
  const keys = path.map((_, index) => nodeKey(path.slice(0, index + 1))).filter((key) => !tree.open.includes(key));
  return keys.length === 0 ? tree : { ...tree, open: [...tree.open, ...keys] };
}

function toggled(keys: readonly string[], key: string): string[] {
  return keys.includes(key) ? keys.filter((entry) => entry !== key) : [...keys, key];
}

/**
 * The open nodes are kept in `localStorage` until the personal configuration, as the theme is (decided at the start of
 * M6): the origin has the port, so they last one run of Kiler.
 */
export const treeStorageKey = 'kiler.tree';

/** The open nodes that were kept, or none when there are none, they do not fit, or the storage cannot be read. */
export function readOpenNodes(storage: Storage | undefined = localStorageOrNothing()): string[] {
  try {
    const saved: unknown = JSON.parse(storage?.getItem(treeStorageKey) ?? '[]');
    return Array.isArray(saved) && saved.every((key) => typeof key === 'string') ? saved : [];
  } catch {
    return [];
  }
}

/** Keeps the open nodes. A storage that refuses only means that they are not kept. */
export function saveOpenNodes(open: readonly string[], storage: Storage | undefined = localStorageOrNothing()): void {
  try {
    storage?.setItem(treeStorageKey, JSON.stringify(open));
  } catch {
    // Private mode or a full storage: the tree still opens on this page.
  }
}

/** `window.localStorage` throws when the browser blocks storage for the page. */
function localStorageOrNothing(): Storage | undefined {
  try {
    return window.localStorage;
  } catch {
    return undefined;
  }
}

/** The name of a stash, and of one that cannot be read. */
export function stashName(stash: { name: string | null }): string {
  return stash.name ?? texts.browse.unreadableStash;
}

/** A stash path as `Payments › Prod`. */
export function pathText(path: readonly PathStash[]): string {
  return path.map(stashName).join(texts.browse.pathSeparator);
}

/** The length of a value in the list, `…` included. */
export const shortLength = 80;

/**
 * A value in the list (docs/api.md, "List item"): its first line, at most `max` characters, with `…` when there is more.
 * The characters are counted whole, so a pair of UTF-16 units is not cut.
 */
export function shorten(text: string, max: number = shortLength): string {
  const trimmed = text.trim();
  const end = trimmed.search(/\r?\n/);
  const line = [...(end === -1 ? trimmed : trimmed.slice(0, end).trimEnd())];
  const more = end !== -1;
  if (line.length > (more ? max - 1 : max)) {
    return `${line
      .slice(0, max - 1)
      .join('')
      .trimEnd()}…`;
  }

  return more ? `${line.join('')}…` : line.join('');
}

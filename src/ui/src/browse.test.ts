import { describe, expect, it } from 'vitest';
import { recordsSearch, type RecordsQuery, type Template, type TemplateField } from './api.ts';
import {
  browsePath,
  draftState,
  fieldFilters,
  hasFilters,
  tagChoices,
  templateChoices,
  withField,
  withoutFilters,
  withTag,
  withTemplate,
  filterKey,
  isNodeOpen,
  isSearchShortcut,
  newDraftId,
  newRecordPath,
  nodePlace,
  pathText,
  pendingSearch,
  queryFromSearch,
  readOpenNodes,
  revealNode,
  saveOpenNodes,
  searchText,
  shorten,
  toggleNode,
  treeState,
  treeTop,
  treeStorageKey,
  typeSearch,
  type Entry,
} from './browse.ts';

/** A storage of its own for each test. */
function memoryStorage(entries: Record<string, string> = {}): Storage {
  const items = new Map(Object.entries(entries));
  return {
    getItem: (key: string) => items.get(key) ?? null,
    setItem: (key: string, value: string) => void items.set(key, value),
  } as unknown as Storage;
}

/** A storage that the browser blocks: every call throws. */
const blocked = {
  getItem: () => {
    throw new DOMException('Blocked', 'SecurityError');
  },
  setItem: () => {
    throw new DOMException('Full', 'QuotaExceededError');
  },
} as unknown as Storage;

describe('queryFromSearch', () => {
  it('is no query for an address without a search', () => {
    expect(queryFromSearch('')).toEqual({});
    expect(queryFromSearch('?')).toEqual({});
  });

  it('reads every part, and recordsSearch writes the same address back', () => {
    const search =
      '?q=%D0%B1%D0%B0%D0%B7%D0%B0%20prod&stash=a1b2c3d4,e5f6g7h8&template=db&tag=payments&tag=%D0%BE%D1%82%D1%87%D0%B5%D1%82%D0%B8&field.env=prod&field.kind=docs';

    const query = queryFromSearch(search);

    expect(query).toEqual({
      q: 'база prod',
      stash: ['a1b2c3d4', 'e5f6g7h8'],
      template: 'db',
      tags: ['payments', 'отчети'],
      fields: { env: 'prod', kind: 'docs' },
    });
    expect(recordsSearch(query)).toBe(search);
  });

  it.each<RecordsQuery>([
    { q: 'oracle' },
    { q: 'база данни' },
    { stash: ['a1b2c3d4'] },
    { template: 'sql', tags: ['a b', 'c&d'] },
    { fields: { 'my key': 'x=y' } },
  ])('gives back the query that recordsSearch wrote: %j', (query) => {
    expect(queryFromSearch(recordsSearch(query))).toEqual(query);
  });

  it('leaves out the blank parts', () => {
    expect(queryFromSearch('?q=%20&stash=&template=&tag=&tag=%20&field.env=&field.=prod')).toEqual({});
  });

  it('reads a part other than tag once, by its first', () => {
    expect(queryFromSearch('?q=one&q=two&field.env=prod&field.env=test')).toEqual({
      q: 'one',
      fields: { env: 'prod' },
    });
  });

  it('keeps a key of a field that names a member of an object as a field of its own', () => {
    const query = queryFromSearch('?field.__proto__=x&field.constructor=y');

    expect(Object.entries(query.fields ?? {})).toEqual([
      ['__proto__', 'x'],
      ['constructor', 'y'],
    ]);
  });

  it('keeps the text of the search as it was typed', () => {
    expect(queryFromSearch('?q=%20two%20words%20').q).toBe(' two words ');
  });
});

describe('the paths', () => {
  it('gives the main view with its query', () => {
    expect(browsePath({})).toBe('/');
    expect(browsePath({ q: 'x', stash: ['a1b2c3d4', 'e5f6g7h8'] })).toBe('/?q=x&stash=a1b2c3d4,e5f6g7h8');
  });

  it('gives the form at the node of the "+", and without a path above the tree', () => {
    expect(newRecordPath(['a1b2c3d4', 'e5f6g7h8'])).toBe('/new?stash=a1b2c3d4,e5f6g7h8');
    expect(newRecordPath(['a1b2c3d4'])).toBe('/new?stash=a1b2c3d4');
    expect(newRecordPath()).toBe('/new');
  });

  it('gives the "+" of a node the path of its place, also of a stash at two places', () => {
    const prod = { id: 'p1p1p1p1', name: 'Prod', count: 1, children: [] };
    const billing = nodePlace(treeTop, { id: 'b1b1b1b1', name: 'Billing', count: 1, children: [prod] });
    const orders = nodePlace(treeTop, { id: 'o1o1o1o1', name: null, count: 1, children: [prod] });
    const underBilling = nodePlace(billing, prod);
    const underOrders = nodePlace(orders, prod);

    expect(newRecordPath(underBilling.path)).toBe('/new?stash=b1b1b1b1,p1p1p1p1');
    expect(newRecordPath(underOrders.path)).toBe('/new?stash=o1o1o1o1,p1p1p1p1');
    expect(underBilling.key).not.toBe(underOrders.key);
    expect(underBilling.names).toEqual(['Billing', 'Prod']);
    expect(underOrders.names).toEqual(['Unreadable stash', 'Prod']);
    expect(
      newRecordPath(nodePlace(underBilling, { id: 'r1r1r1r1', name: 'Reports', count: 1, children: [] }).path),
    ).toBe('/new?stash=b1b1b1b1,p1p1p1p1,r1r1r1r1');
  });

  it('writes a path with a stash that cannot be read', () => {
    expect(
      pathText([
        { id: 'a1b2c3d4', name: 'Payments' },
        { id: 'e5f6g7h8', name: null },
      ]),
    ).toBe('Payments › Unreadable stash');
  });
});

/** A field of a template with only what the filters read. */
function field(key: string, label: string, filter: boolean): TemplateField {
  return {
    key,
    label,
    type: 'select',
    level: 0,
    levelByEnv: null,
    required: false,
    filter,
    list: false,
    options: ['prod', 'test'],
    language: null,
    dialect: null,
    languages: null,
    refTemplate: null,
  };
}

/** The templates by id, in the order of `GET /api/templates`, by name. */
const templates = new Map<string, Template>([
  [
    'db',
    {
      id: 'db',
      name: 'Database server',
      icon: 'database',
      fields: [field('env', 'Environment', true), field('kind', 'Kind of server', false)],
    },
  ],
  ['link', { id: 'link', name: 'Link', icon: 'link', fields: [field('kind', 'Kind of link', true)] }],
  ['sql', { id: 'sql', name: 'SQL query', icon: 'code', fields: [field('env', 'Database environment', true)] }],
]);

describe('the filters', () => {
  const base: RecordsQuery = { q: 'billing', stash: ['a1b2c3d4'] };

  it('chooses and clears a type in the query, and keeps the search and the node', () => {
    const chosen = withTemplate(base, 'db');

    expect(browsePath(chosen)).toBe('/?q=billing&stash=a1b2c3d4&template=db');
    expect(hasFilters(chosen)).toBe(true);
    expect(browsePath(withTemplate(chosen, undefined))).toBe('/?q=billing&stash=a1b2c3d4');
    expect(hasFilters(withTemplate(chosen, undefined))).toBe(false);
  });

  it('chooses and clears a value of a field in the query', () => {
    const chosen = withField(withField(base, 'env', 'prod'), 'kind', 'docs');

    expect(browsePath(chosen)).toBe('/?q=billing&stash=a1b2c3d4&field.env=prod&field.kind=docs');
    expect(browsePath(withField(chosen, 'env', 'test'))).toBe(
      '/?q=billing&stash=a1b2c3d4&field.env=test&field.kind=docs',
    );
    expect(browsePath(withField(chosen, 'env', undefined))).toBe('/?q=billing&stash=a1b2c3d4&field.kind=docs');
  });

  it('chooses several tags, and clears one without regard to case', () => {
    const chosen = withTag(withTag(base, 'payments', true), 'отчети', true);

    expect(chosen.tags).toEqual(['payments', 'отчети']);
    expect(browsePath(chosen)).toBe('/?q=billing&stash=a1b2c3d4&tag=payments&tag=%D0%BE%D1%82%D1%87%D0%B5%D1%82%D0%B8');
    expect(withTag(chosen, 'ОТЧЕТИ', false).tags).toEqual(['payments']);
    expect(withTag(chosen, 'Payments', true).tags).toEqual(['отчети', 'Payments']);
    expect(hasFilters(withTag(withTag(chosen, 'payments', false), 'отчети', false))).toBe(false);
  });

  it('clears every filter at once, and keeps the search and the node', () => {
    const chosen = withField(withTag(withTemplate(base, 'db'), 'payments', true), 'env', 'prod');

    expect(withoutFilters(chosen)).toEqual({ ...base, template: undefined, tags: undefined, fields: undefined });
    expect(browsePath(withoutFilters(chosen))).toBe('/?q=billing&stash=a1b2c3d4');
  });

  it('gives the types by the names of their templates, and by the id one that is not there', () => {
    const choices = templateChoices(
      [
        { id: 'sql', count: 3 },
        { id: 'db', count: 5 },
        { id: 'gone', count: 1 },
      ],
      templates,
      'db',
    );

    expect(choices).toEqual([
      { value: 'db', label: 'Database server', count: 5, chosen: true },
      { value: 'gone', label: 'gone', count: 1, chosen: false },
      { value: 'sql', label: 'SQL query', count: 3, chosen: false },
    ]);
    expect(templateChoices([{ id: 'db', count: 5 }], undefined, undefined)[0]?.label).toBe('db');
  });

  it('keeps a type, a tag and a value chosen that no record has now, at 0, so they can be cleared', () => {
    expect(templateChoices([{ id: 'sql', count: 3 }], templates, 'link')).toContainEqual({
      value: 'link',
      label: 'Link',
      count: 0,
      chosen: true,
    });
    expect(tagChoices([], ['payments', 'Payments'])).toEqual([
      { value: 'payments', label: 'payments', count: 0, chosen: true },
    ]);
    expect(fieldFilters([], templates, { env: 'prod' })).toEqual([
      { key: 'env', label: 'Environment', choices: [{ value: 'prod', label: 'prod', count: 0, chosen: true }] },
    ]);
  });

  it('marks the tags chosen without regard to case', () => {
    expect(
      tagChoices(
        [
          { tag: 'Payments', count: 4 },
          { tag: 'reports', count: 2 },
        ],
        ['payments'],
      ),
    ).toEqual([
      { value: 'Payments', label: 'Payments', count: 4, chosen: true },
      { value: 'reports', label: 'reports', count: 2, chosen: false },
    ]);
  });

  it('gives a field the label of the first template that filters by it, and its key without one', () => {
    const filters = fieldFilters(
      [
        {
          key: 'env',
          values: [
            { value: 'Prod', count: 4 },
            { value: 'test', count: 2 },
          ],
        },
        { key: 'kind', values: [{ value: 'docs', count: 1 }] },
        { key: 'owner', values: [{ value: 'team', count: 1 }] },
      ],
      templates,
      { env: 'prod' },
    );

    expect(filters.map(({ key, label }) => [key, label])).toEqual([
      ['env', 'Environment'],
      ['kind', 'Kind of link'],
      ['owner', 'owner'],
    ]);
    expect(filters[0]?.choices).toEqual([
      { value: 'Prod', label: 'Prod', count: 4, chosen: true },
      { value: 'test', label: 'test', count: 2, chosen: false },
    ]);
    expect(fieldFilters([{ key: 'env', values: [] }], undefined, {})).toEqual([
      { key: 'env', label: 'env', choices: [] },
    ]);
  });
});

describe('filterKey', () => {
  it('is blank without a search and a filter, also with a node chosen', () => {
    expect(filterKey({})).toBe('');
    expect(filterKey({ stash: ['a1b2c3d4'] })).toBe('');
  });

  it.each<RecordsQuery>([{ q: 'x' }, { template: 'db' }, { tags: ['a'] }, { fields: { env: 'prod' } }])(
    'names a search or a filter: %j',
    (query) => {
      expect(filterKey({ ...query, stash: ['a1b2c3d4'] })).toBe(recordsSearch(query));
    },
  );
});

describe('the tree', () => {
  it('starts collapsed to level 1', () => {
    const tree = treeState(readOpenNodes(memoryStorage()));

    expect(isNodeOpen(tree, 'a', '')).toBe(false);
  });

  it('keeps its open nodes', () => {
    const storage = memoryStorage();
    let tree = treeState(readOpenNodes(storage));

    tree = toggleNode(tree, 'a', '');
    tree = toggleNode(tree, 'a,b', '');
    tree = toggleNode(tree, 'c', '');
    tree = toggleNode(tree, 'c', '');
    saveOpenNodes(tree.open, storage);
    const again = treeState(readOpenNodes(storage));

    expect(again.open).toEqual(['a', 'a,b']);
    expect(isNodeOpen(again, 'a,b', '')).toBe(true);
    expect(isNodeOpen(again, 'c', '')).toBe(false);
  });

  it('shows every branch with results open during a search, and the earlier state after it', () => {
    let tree = toggleNode(treeState([]), 'a', '');

    expect(isNodeOpen(tree, 'b', '?q=x')).toBe(true);
    expect(isNodeOpen(tree, 'a,c', '?q=x')).toBe(true);

    tree = toggleNode(tree, 'a', '?q=x');
    tree = toggleNode(tree, 'b', '?q=x');

    expect(isNodeOpen(tree, 'a', '?q=x')).toBe(false);
    expect(isNodeOpen(tree, 'b', '?q=x')).toBe(false);
    expect(isNodeOpen(tree, 'a', '')).toBe(true);
    expect(isNodeOpen(tree, 'b', '')).toBe(false);
    expect(tree.open).toEqual(['a']);
  });

  it('opens every branch again for another search', () => {
    const tree = toggleNode(treeState([]), 'a', '?q=x');

    expect(isNodeOpen(tree, 'a', '?q=x')).toBe(false);
    expect(isNodeOpen(tree, 'a', '?q=xy')).toBe(true);
    expect(isNodeOpen(toggleNode(tree, 'b', '?q=xy'), 'a', '?q=xy')).toBe(true);
  });

  it('opens a chosen node and those above it, once', () => {
    const tree = revealNode(treeState(['a']), ['a', 'b', 'c']);

    expect(tree.open).toEqual(['a', 'a,b', 'a,b,c']);
    expect(revealNode(tree, ['a', 'b'])).toBe(tree);
    expect(revealNode(tree, [])).toBe(tree);
  });

  it.each(['not json', '{"a":1}', '["a",1]', 'null'])(
    'keeps no open nodes from a saved value that does not fit: %s',
    (saved) => {
      expect(readOpenNodes(memoryStorage({ [treeStorageKey]: saved }))).toEqual([]);
    },
  );

  it('keeps no open nodes when the storage cannot be used', () => {
    expect(readOpenNodes(blocked)).toEqual([]);
    expect(readOpenNodes(undefined)).toEqual([]);
    expect(() => saveOpenNodes(['a'], blocked)).not.toThrow();
  });
});

describe('the search field', () => {
  const start: Entry = { key: 'k1', state: null };

  it('shows the search of the address without a draft', () => {
    expect(searchText(null, start, 'oracle')).toBe('oracle');
    expect(pendingSearch(null, start, 'oracle')).toBeNull();
  });

  it('shows what is typed, until its wait sends it to the address', () => {
    const draft = typeSearch(null, start, 'база', 'd1');

    expect(searchText(draft, start, '')).toBe('база');
    expect(pendingSearch(draft, start, '')).toBe(draft);

    const written: Entry = { key: 'k2', state: draftState(draft) };

    expect(searchText(draft, written, 'база')).toBe('база');
    expect(pendingSearch(draft, written, 'база')).toBeNull();
  });

  it('keeps a key typed while the address changes', () => {
    const first = typeSearch(null, start, 'ab', 'd1');
    const second = typeSearch(first, start, 'abc', 'd2');
    const written: Entry = { key: 'k2', state: draftState(first) };

    expect(second.id).toBe('d1');
    expect(searchText(second, written, 'ab')).toBe('abc');
    expect(pendingSearch(second, written, 'ab')).toBe(second);
  });

  it('shows the search of another address, such as one of Back, and begins a new draft there', () => {
    const draft = typeSearch(null, start, 'abc', 'd1');
    const back: Entry = { key: 'k0', state: null };

    expect(searchText(draft, back, 'old')).toBe('old');
    expect(pendingSearch(draft, back, 'old')).toBeNull();
    expect(typeSearch(draft, back, 'older', 'd2')).toEqual({ text: 'older', id: 'd2', at: 'k0' });
  });

  it('shows the search of an address that an earlier draft wrote', () => {
    const earlier = typeSearch(null, start, 'ab', 'd1');
    const draft = typeSearch(null, { key: 'k3', state: null }, 'xyz', 'd2');

    expect(searchText(draft, { key: 'k4', state: draftState(earlier) }, 'ab')).toBe('ab');
  });

  it('gives each draft an id of its own, which no other load of the page gives', () => {
    expect(newDraftId()).not.toBe(newDraftId());
  });

  it.each([
    [{ code: 'KeyK', ctrlKey: true, metaKey: false, altKey: false, shiftKey: false }, true],
    [{ code: 'KeyK', ctrlKey: false, metaKey: true, altKey: false, shiftKey: false }, true],
    [{ code: 'KeyK', ctrlKey: false, metaKey: false, altKey: false, shiftKey: false }, false],
    [{ code: 'KeyK', ctrlKey: true, metaKey: false, altKey: false, shiftKey: true }, false],
    [{ code: 'KeyK', ctrlKey: true, metaKey: false, altKey: true, shiftKey: false }, false],
    [{ code: 'KeyL', ctrlKey: true, metaKey: false, altKey: false, shiftKey: false }, false],
  ])('takes Ctrl+K by the place of the key: %j', (event, shortcut) => {
    expect(isSearchShortcut(event)).toBe(shortcut);
  });
});

describe('shorten', () => {
  it('keeps a short value', () => {
    expect(shorten('db01.example.com')).toBe('db01.example.com');
    expect(shorten('')).toBe('');
  });

  it('cuts a long value to its length, with …', () => {
    const short = shorten('x'.repeat(200));

    expect(short).toBe(`${'x'.repeat(79)}…`);
    expect(shorten('x'.repeat(80))).toBe('x'.repeat(80));
    expect(shorten('abc def', 5)).toBe('abc…');
  });

  it('keeps the first line, with …', () => {
    expect(shorten('SELECT *\r\nFROM payments')).toBe('SELECT *…');
    expect(shorten('\n  first  \nsecond')).toBe('first…');
    expect(shorten('abcde\nf', 5)).toBe('abcd…');
  });

  it('cuts no character in two', () => {
    expect(shorten('😀😀😀', 2)).toBe('😀…');
    expect(shorten('щ'.repeat(10), 4)).toBe('щщщ…');
  });
});

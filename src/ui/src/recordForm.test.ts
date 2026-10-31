import { describe, expect, it } from 'vitest';
import type { LevelName, RecordData, RecordField, RecordValue, Template, TemplateField } from './api.ts';
import {
  addTags,
  checkDraft,
  conflictKey,
  defaultLevel,
  editDraft,
  fieldViews,
  levelOf,
  newDraft,
  newRecordFormState,
  overwriteRequest,
  recordFormReducer,
  recordRequest,
  refQuery,
  suggestedEnv,
  tagsOf,
  tagSuggestions,
  type Draft,
  type DraftValue,
  type RecordFormAction,
  type RecordFormState,
} from './recordForm.ts';
import { texts } from './texts.ts';

const levels: LevelName[] = [
  { level: 0, name: 'Guest' },
  { level: 1, name: 'Team' },
  { level: 2, name: 'Test' },
  { level: 3, name: 'Prod' },
];

function field(key: string, type: TemplateField['type'], settings: Partial<TemplateField> = {}): TemplateField {
  return {
    key,
    label: key,
    type,
    level: 1,
    levelByEnv: null,
    required: false,
    filter: false,
    list: false,
    options: null,
    language: null,
    dialect: null,
    languages: null,
    refTemplate: null,
    ...settings,
  };
}

const byEnv = { prod: 3, test: 2, dev: 1 };

const db: Template = {
  id: 'db',
  name: 'Server / database',
  icon: 'database',
  fields: [
    field('env', 'select', { level: 0, options: ['prod', 'test', 'dev'] }),
    field('host', 'text', { required: true }),
    field('port', 'number'),
    field('user', 'text', { level: null, levelByEnv: byEnv }),
    field('password', 'secret', { level: null, levelByEnv: byEnv }),
  ],
};

const every: Template = {
  id: 'every',
  name: 'Every type',
  icon: 'note',
  fields: [
    field('text', 'text'),
    field('multiline', 'multiline'),
    field('markdown', 'markdown'),
    field('doc', 'doc'),
    field('select', 'select', { options: ['a', 'b'] }),
    field('number', 'number'),
    field('date', 'date'),
    field('url', 'url'),
    field('code', 'code', { language: 'sql', dialect: 'plsql' }),
    field('script', 'code', { languages: ['bash', 'powershell'] }),
    field('ref', 'ref', { refTemplate: 'db' }),
    field('secret', 'secret'),
  ],
};

function value(key: string, level: number, recordValue: RecordValue): RecordField {
  return { key, level, levelName: null, kind: 'value', value: recordValue };
}

function other(key: string, level: number, kind: 'empty' | 'secret' | 'locked' | 'unreadable'): RecordField {
  return { key, level, levelName: kind === 'locked' ? 'Prod' : null, kind, value: null };
}

const author = { account: 'demo.editor', name: 'Demo Editor' };

function record(fields: RecordField[], description: RecordField = other('description', 0, 'empty')): RecordData {
  return {
    id: 'k3x9q2mf',
    template: 'db',
    version: 7,
    stash: [{ id: 'a1b2c3d4', name: 'Alpha' }],
    name: 'Alpha test database',
    description,
    tags: ['alpha'],
    fields,
    levels: [1, 2],
    createdBy: author,
    createdAt: '2026-10-21T11:32:00Z',
    updatedBy: author,
    updatedAt: '2026-10-21T11:32:00Z',
  };
}

const testRecord = record([
  value('env', 0, 'test'),
  value('host', 1, 'db01.example.test'),
  value('port', 1, 1521),
  value('user', 2, 'alpha_read'),
  other('password', 2, 'secret'),
]);

const prodRecord = record([
  value('env', 0, 'prod'),
  value('host', 2, 'db02.example.test'),
  other('port', 1, 'empty'),
  other('user', 3, 'locked'),
  other('password', 3, 'locked'),
]);

/** The editor of the tests holds the levels 0 to 2 (Test). */
const top = 2;

function set(draft: Draft, key: string, draftValue: DraftValue): Draft {
  return { ...draft, values: { ...draft.values, [key]: draftValue } };
}

function text(draft: Draft, key: string, typed: string): Draft {
  return set(draft, key, { kind: 'text', text: typed });
}

function viewOf(draft: Draft, key: string) {
  const view = fieldViews(draft, top, levels).find((entry) => entry.field.key === key);
  if (view === undefined) {
    throw new Error(`No field ${key}`);
  }

  return view;
}

function request(draft: Draft) {
  return recordRequest(draft, ['a1b2c3d4'], top, levels);
}

describe('newDraft', () => {
  it('has no template and no fields until one is chosen', () => {
    expect(newDraft(null)).toMatchObject({ template: null, start: null, name: '', tags: [], values: {} });
  });

  it('makes every field of the template empty, the description too', () => {
    const draft = newDraft(every);

    expect(draft.values).toEqual({
      description: { kind: 'text', text: '' },
      text: { kind: 'text', text: '' },
      multiline: { kind: 'text', text: '' },
      markdown: { kind: 'text', text: '' },
      doc: { kind: 'text', text: '' },
      select: { kind: 'text', text: '' },
      number: { kind: 'text', text: '' },
      date: { kind: 'text', text: '' },
      url: { kind: 'text', text: '' },
      code: { kind: 'code', code: '', language: '' },
      script: { kind: 'code', code: '', language: 'bash' },
      ref: { kind: 'ref', ref: null },
      secret: { kind: 'secret', typed: '', kept: false, clear: false, again: false },
    });
  });

  it('keeps the header and the description when another template is chosen', () => {
    const state = run(
      newRecordFormState(newDraft(every)),
      { type: 'name', text: 'Alpha' },
      { type: 'value', key: 'description', value: { kind: 'text', text: 'About it' } },
      { type: 'value', key: 'text', value: { kind: 'text', text: 'lost' } },
      { type: 'template', template: db },
    );

    expect(state.draft.template).toBe(db);
    expect(state.draft.name).toBe('Alpha');
    expect(state.draft.values.description).toEqual({ kind: 'text', text: 'About it' });
    expect(state.draft.values.text).toBeUndefined();
    expect(state.draft.values.host).toEqual({ kind: 'text', text: '' });
  });
});

describe('editDraft', () => {
  it('starts from the record: the header, the values, a secret kept, and how each field started', () => {
    const draft = editDraft(testRecord, db);

    expect(draft.name).toBe('Alpha test database');
    expect(draft.tags).toEqual(['alpha']);
    expect(draft.start).toEqual({
      version: 7,
      env: 'test',
      fields: {
        description: { level: 0, kind: 'empty' },
        env: { level: 0, kind: 'value' },
        host: { level: 1, kind: 'value' },
        port: { level: 1, kind: 'value' },
        user: { level: 2, kind: 'value' },
        password: { level: 2, kind: 'secret' },
      },
    });
    expect(draft.values).toEqual({
      description: { kind: 'text', text: '' },
      env: { kind: 'text', text: 'test' },
      host: { kind: 'text', text: 'db01.example.test' },
      port: { kind: 'text', text: '1521' },
      user: { kind: 'text', text: 'alpha_read' },
      password: { kind: 'secret', typed: '', kept: true, clear: false, again: false },
    });
  });

  it('takes a code, a code with its language and a ref, and a value of another shape by its text', () => {
    const draft = editDraft(
      record([
        value('text', 1, 42),
        value('code', 1, { language: 'sql', code: 'select 1' }),
        value('script', 1, { language: 'powershell', code: 'Get-Date' }),
        value('ref', 1, { id: 'e5f6g7h8', name: 'Alpha database' }),
      ]),
      every,
    );

    expect(draft.values.text).toEqual({ kind: 'text', text: '42' });
    expect(draft.values.code).toEqual({ kind: 'code', code: 'select 1', language: '' });
    expect(draft.values.script).toEqual({ kind: 'code', code: 'Get-Date', language: 'powershell' });
    expect(draft.values.ref).toEqual({ kind: 'ref', ref: { id: 'e5f6g7h8', name: 'Alpha database' } });
    expect(draft.values.secret).toEqual({ kind: 'secret', typed: '', kept: false, clear: false, again: false });
  });

  it('keeps a field above the editor as locked, with the name of its level', () => {
    const draft = editDraft(prodRecord, db);

    expect(draft.values.password).toEqual({ kind: 'locked', levelName: 'Prod' });
    expect(viewOf(draft, 'password')).toMatchObject({ padlock: 'Prod', level: 3, choices: [] });
  });
});

describe('the levels', () => {
  it('takes the default by env, the level without one, and the highest of levelByEnv for an empty env', () => {
    const user = field('user', 'text', { level: null, levelByEnv: byEnv });

    expect(defaultLevel(user, 'test')).toBe(2);
    expect(defaultLevel(user, null)).toBe(3);
    expect(defaultLevel(user, 'staging')).toBe(3);
    expect(defaultLevel(field('host', 'text', { level: 1, levelByEnv: { prod: 3 } }), 'test')).toBe(1);
  });

  it('follows the env of the draft', () => {
    const draft = text(newDraft(db), 'env', 'dev');

    expect(viewOf(draft, 'user')).toMatchObject({ level: 1, choices: [1, 2] });
    expect(viewOf(text(draft, 'env', 'test'), 'user')).toMatchObject({ level: 2, choices: [2] });
  });

  it('raises a field up to the top level of the editor, and no lower than its default', () => {
    const draft = newDraft(db);
    const raised = run(newRecordFormState(draft), { type: 'level', key: 'host', level: 2 }).draft;

    expect(viewOf(draft, 'host').choices).toEqual([1, 2]);
    expect(levelOf(raised, db.fields[1])).toBe(2);
    expect(levelOf({ ...raised, asked: { host: 0 } }, db.fields[1])).toBe(1);
  });

  it('gives env no level to ask, as it stays at level 0', () => {
    expect(viewOf(newDraft(db), 'env').choices).toEqual([0]);
  });

  it('keeps the raise of an edit that the editor did not change, also at a change of env', () => {
    const draft = editDraft(prodRecord, db);

    expect(levelOf(draft, db.fields[1])).toBe(2);
    expect(levelOf(text(draft, 'env', 'dev'), db.fields[1])).toBe(2);
  });

  it('moves a field of an edit that is not raised with the env', () => {
    const draft = editDraft(testRecord, db);

    expect(levelOf(draft, db.fields[3])).toBe(2);
    expect(levelOf(text(draft, 'env', 'dev'), db.fields[3])).toBe(1);
  });
});

describe('the fields above the editor', () => {
  it('show the padlock of a new record whose env puts them above, and are not sent', () => {
    const draft = set(text(text(newDraft(db), 'env', 'prod'), 'user', 'typed before'), 'password', {
      kind: 'secret',
      typed: 'made-up',
      kept: false,
      clear: false,
      again: false,
    });

    expect(viewOf(draft, 'user')).toMatchObject({ padlock: 'Prod', choices: [] });
    expect(request(draft).fields).not.toHaveProperty('user');
    expect(request(draft).fields).not.toHaveProperty('password');
  });

  it('are left out of an edit, and the description above is not given', () => {
    const draft = editDraft(record(prodRecord.fields, other('description', 3, 'locked')), db);

    expect(request(draft)).toMatchObject({ description: null, fields: { env: 'prod', host: 'db02.example.test' } });
    expect(request(draft).fields).not.toHaveProperty('user');
    expect(request(draft).fields).not.toHaveProperty('password');
    expect(viewOf(draft, 'description').padlock).toBe('Prod');
  });

  it('stay in an edit when the record has a value that a change of env moves above, and Kiler says why', () => {
    const draft = text(editDraft(testRecord, db), 'env', 'prod');

    expect(viewOf(draft, 'user')).toMatchObject({ padlock: null, level: 3 });
    expect(request(draft).fields).toMatchObject({ user: 'alpha_read' });
  });

  it('show the padlock of an empty field of an edit that is above', () => {
    const draft = editDraft(record([value('env', 0, 'prod'), other('user', 3, 'empty')]), db);

    expect(viewOf(draft, 'user')).toMatchObject({ padlock: 'Prod', level: 3 });
  });

  it('keep the level that the record has for a locked field', () => {
    expect(levelOf(text(editDraft(prodRecord, db), 'env', 'dev'), db.fields[3])).toBe(3);
  });

  it('leave out an unreadable field', () => {
    const draft = editDraft(record([other('host', 1, 'unreadable')]), db);

    expect(request(draft).fields).not.toHaveProperty('host');
  });
});

describe('recordRequest', () => {
  it('sends each type as the API takes it', () => {
    let draft = newDraft(every);
    for (const [key, typed] of [
      ['text', ' a text '],
      ['multiline', 'line 1\nline 2'],
      ['markdown', '# Title'],
      ['doc', 'C:\\docs\\a.md'],
      ['select', 'b'],
      ['number', ' 1521 '],
      ['date', '2026-10-21'],
      ['url', ' https://example.test/ '],
    ] as const) {
      draft = text(draft, key, typed);
    }

    draft = set(draft, 'code', { kind: 'code', code: 'select 1', language: '' });
    draft = set(draft, 'script', { kind: 'code', code: 'Get-Date', language: 'powershell' });
    draft = set(draft, 'ref', { kind: 'ref', ref: { id: 'e5f6g7h8', name: 'Alpha database' } });
    draft = set(draft, 'secret', { kind: 'secret', typed: 'made-up', kept: false, clear: false, again: false });

    expect(request({ ...draft, name: ' Alpha ', tags: ['alpha'] })).toEqual({
      stash: ['a1b2c3d4'],
      name: 'Alpha',
      description: null,
      tags: ['alpha'],
      fields: {
        text: ' a text ',
        multiline: 'line 1\nline 2',
        markdown: '# Title',
        doc: 'C:\\docs\\a.md',
        select: 'b',
        number: 1521,
        date: '2026-10-21',
        url: 'https://example.test/',
        code: 'select 1',
        script: { language: 'powershell', code: 'Get-Date' },
        ref: 'e5f6g7h8',
        secret: 'made-up',
      },
      levels: {},
    });
  });

  it('sends a blank text, an empty code and a ref not chosen as no value, and leaves out a secret not typed', () => {
    const draft = text(newDraft(every), 'text', '   ');

    expect(request(draft).fields).toEqual({
      text: null,
      multiline: null,
      markdown: null,
      doc: null,
      select: null,
      number: null,
      date: null,
      url: null,
      code: null,
      script: null,
      ref: null,
    });
  });

  it('sends the description as typed', () => {
    expect(request(text(newDraft(db), 'description', 'About it')).description).toBe('About it');
  });

  describe('a secret of an edit', () => {
    const draft = editDraft(testRecord, db);
    const secret = (changes: Partial<Extract<DraftValue, { kind: 'secret' }>>): Draft =>
      set(draft, 'password', { kind: 'secret', typed: '', kept: true, clear: false, again: false, ...changes });

    it('is kept: left out', () => {
      expect(request(draft).fields).not.toHaveProperty('password');
    });

    it('is cleared: null', () => {
      expect(request(secret({ clear: true })).fields.password).toBeNull();
    });

    it('is new: the value typed', () => {
      expect(request(secret({ typed: 'made-up-new' })).fields.password).toBe('made-up-new');
    });
  });

  it('sends in levels only the levels that the editor chose', () => {
    const draft = editDraft(prodRecord, db);
    const state = run(
      newRecordFormState(draft),
      { type: 'level', key: 'port', level: 2 },
      { type: 'level', key: 'description', level: 1 },
    );

    expect(request(draft).levels).toEqual({});
    expect(request(state.draft).levels).toEqual({ port: 2, description: 1 });
  });
});

describe('checkDraft', () => {
  const ready = text({ ...newDraft(db), name: 'Alpha' }, 'host', 'db01.example.test');
  const path = { ids: ['a1b2c3d4'], typed: '' };

  it('passes a draft with what it needs', () => {
    expect(checkDraft(ready, path, top, levels)).toEqual({});
  });

  it('asks for a template, a stash and a name', () => {
    expect(checkDraft(newDraft(null), { ids: [], typed: '' }, top, levels)).toEqual({
      template: texts.form.problems.template,
      stash: texts.form.problems.stash,
      name: texts.form.problems.name,
    });
  });

  it('refuses a name of a stash typed and not chosen', () => {
    expect(checkDraft(ready, { ids: ['a1b2c3d4'], typed: 'Half' }, top, levels)).toEqual({
      stash: texts.form.problems.stashTyped,
    });
  });

  it('asks for a required field that is empty', () => {
    expect(checkDraft(text(ready, 'host', '  '), path, top, levels)).toEqual({ host: texts.form.problems.required });
  });

  it('takes a required secret that an edit keeps, but not one that it clears', () => {
    const template: Template = { ...db, fields: [field('password', 'secret', { required: true })] };
    const draft = editDraft(record([other('password', 1, 'secret')]), template);
    const cleared = set(draft, 'password', { kind: 'secret', typed: '', kept: true, clear: true, again: false });

    expect(checkDraft(draft, path, top, levels)).toEqual({});
    expect(checkDraft(cleared, path, top, levels)).toEqual({ password: texts.form.problems.required });
  });

  it('does not ask for a required field above the editor', () => {
    const template: Template = { ...db, fields: [field('host', 'text', { level: 3, required: true })] };

    expect(checkDraft({ ...newDraft(template), name: 'Alpha' }, path, top, levels)).toEqual({});
  });

  it('asks again for a secret that a refused save sent, until it is typed or cleared', () => {
    const again = { kind: 'secret', typed: '', kept: true, clear: false, again: true } as const;
    const draft = set(editDraft(testRecord, db), 'password', again);

    expect(checkDraft(draft, path, top, levels)).toEqual({ password: texts.form.problems.again });
    expect(checkDraft(set(draft, 'password', { ...again, typed: 'made-up' }), path, top, levels)).toEqual({});
    expect(checkDraft(set(draft, 'password', { ...again, clear: true }), path, top, levels)).toEqual({});
  });

  it('asks for a number', () => {
    expect(checkDraft(text(ready, 'port', '15x'), path, top, levels)).toEqual({ port: texts.form.problems.number });
  });
});

describe('recordFormReducer', () => {
  const typed = set(editDraft(testRecord, db), 'password', {
    kind: 'secret',
    typed: 'made-up-new',
    kept: true,
    clear: false,
    again: false,
  });

  it('focuses the first problem in the order of the form', () => {
    const state = run(newRecordFormState(newDraft(db)), {
      type: 'problems',
      problems: { host: 'x', name: 'y' },
    });

    expect(state.focus).toBe('name');
    expect(state.round).toBe(1);
  });

  it('takes the problem of a field away when it changes', () => {
    const state = run(
      newRecordFormState(newDraft(db)),
      { type: 'problems', problems: { host: 'x', name: 'y' } },
      { type: 'value', key: 'host', value: { kind: 'text', text: 'h' } },
      { type: 'touch', key: 'name' },
    );

    expect(state.problems).toEqual({});
  });

  it('sends one request at a time, and keeps no value typed into a secret once it is sent', () => {
    const state = run(
      newRecordFormState(typed),
      { type: 'send', sent: ['password'] },
      { type: 'name', text: 'Changed meanwhile' },
    );

    expect(state.sending).toBe(true);
    expect(state.draft.name).toBe('Alpha test database');
    expect(state.draft.values.password).toMatchObject({ typed: '', again: true });
  });

  it('drops the value typed into a secret that was not sent, and does not ask for it again', () => {
    const state = run(newRecordFormState(typed), { type: 'send', sent: ['host'] });

    expect(state.draft.values.password).toMatchObject({ typed: '', again: false });
  });

  it('shows a refusal with field at that field, and asks for the secret again', () => {
    const state = run(
      newRecordFormState(typed),
      { type: 'send', sent: ['password'] },
      { type: 'refused', message: 'host must be a text.', field: 'host', current: null },
    );

    expect(state.sending).toBe(false);
    expect(state.error).toBeUndefined();
    expect(state.problems).toEqual({ host: 'host must be a text.', password: texts.form.problems.again });
    expect(state.focus).toBe('host');
  });

  it('shows a refusal without field by its sentence, and one of a key that the form does not have too', () => {
    const plain = run(
      newRecordFormState(editDraft(testRecord, db)),
      { type: 'send', sent: [] },
      { type: 'refused', message: 'The shared folder cannot be reached.', field: null, current: null },
    );
    const unknown = run(
      newRecordFormState(editDraft(testRecord, db)),
      { type: 'send', sent: [] },
      { type: 'refused', message: 'gone is not a field of the template.', field: 'gone', current: null },
    );

    expect(plain).toMatchObject({ error: 'The shared folder cannot be reached.', problems: {}, focus: undefined });
    expect(unknown).toMatchObject({ error: 'gone is not a field of the template.', problems: {} });
  });

  it('changes the template of a new record only', () => {
    const edit = newRecordFormState(editDraft(testRecord, db));

    expect(run(edit, { type: 'template', template: every })).toBe(edit);
  });
});

describe('the conflict', () => {
  const edited = text(editDraft(testRecord, db), 'host', 'db03.example.test');
  const current: RecordData = {
    ...testRecord,
    version: 9,
    fields: [...testRecord.fields.slice(0, 1), value('host', 1, 'db02.example.test'), ...testRecord.fields.slice(2)],
    updatedBy: { account: 'demo.admin', name: 'Demo Admin' },
  };
  const conflicted = run(
    newRecordFormState(edited),
    { type: 'send', sent: [] },
    { type: 'refused', message: 'The record has been changed meanwhile.', field: null, current },
  );

  it('keeps the record as it is now, and focuses its buttons', () => {
    expect(conflicted).toMatchObject({ sending: false, error: undefined, conflict: current, focus: conflictKey });
  });

  it('"Overwrite" sends the values of the editor over the version of current', () => {
    const request = overwriteRequest(conflicted.draft, ['a1b2c3d4'], top, levels, current);

    expect(request.version).toBe(9);
    expect(request.fields).toMatchObject({ env: 'test', host: 'db03.example.test', port: 1521 });
    expect(run(conflicted, { type: 'send', sent: [] }).conflict).toBeUndefined();
  });

  it('"Reload" takes the form of current, its version too', () => {
    const draft = editDraft(current, db);

    expect(draft.start?.version).toBe(9);
    expect(draft.values.host).toEqual({ kind: 'text', text: 'db02.example.test' });
  });

  it('shows a 409 without current by its sentence', () => {
    const state = run(
      newRecordFormState(edited),
      { type: 'send', sent: [] },
      {
        type: 'refused',
        message: 'The record is being written now. Try again in a moment.',
        field: null,
        current: null,
      },
    );

    expect(state).toMatchObject({
      conflict: undefined,
      error: 'The record is being written now. Try again in a moment.',
    });
  });
});

describe('tagsOf', () => {
  it('splits at commas, trims and leaves out the blank ones', () => {
    expect(tagsOf(' alpha, beta ,, ')).toEqual(['alpha', 'beta']);
  });
});

describe('the tags', () => {
  const known = ['alpha', 'Beta', 'reports', 'отчети'];

  it('suggests the tags with the typed text, without regard to case, in Latin and in Cyrillic', () => {
    expect(tagSuggestions(known, [], ' A ')).toEqual(['alpha', 'Beta']);
    expect(tagSuggestions(known, [], 'ОТЧ')).toEqual(['отчети']);
    expect(tagSuggestions(known, [], '')).toEqual(known);
  });

  it('suggests no tag that the record has, without regard to case', () => {
    expect(tagSuggestions(known, ['ALPHA'], 'a')).toEqual(['Beta']);
  });

  it('adds a new tag, and each once without regard to case', () => {
    expect(addTags(['alpha'], ' new tag ')).toEqual(['alpha', 'new tag']);
    expect(addTags(['alpha'], 'Alpha, beta, BETA,')).toEqual(['alpha', 'beta']);
    expect(addTags(['alpha'], '  ')).toEqual(['alpha']);
  });
});

describe('refQuery', () => {
  it('searches the records of the template of the ref', () => {
    expect(refQuery(field('db', 'ref', { refTemplate: 'db' }), 'alpha')).toEqual({ template: 'db', q: 'alpha' });
  });
});

describe('suggestedEnv', () => {
  const draft = newDraft(db);
  const path = (...names: (string | null)[]) => names.map((name) => ({ name }));

  it('suggests the option of env that a stash of the path is named, without regard to case', () => {
    expect(suggestedEnv(draft, path('Alpha', 'Prod'))).toBe('prod');
    expect(suggestedEnv(draft, path(' TEST ', 'Reports'))).toBe('test');
  });

  it('takes the deepest stash that is named so', () => {
    expect(suggestedEnv(draft, path('Dev', 'Alpha', 'Prod'))).toBe('prod');
  });

  it('suggests nothing over a value of env', () => {
    expect(suggestedEnv(text(draft, 'env', 'dev'), path('Prod'))).toBeNull();
  });

  it('suggests nothing without a stash of that name, a stash without a name, or a template without env', () => {
    expect(suggestedEnv(draft, path('Alpha', null))).toBeNull();
    expect(suggestedEnv(newDraft(every), path('Prod'))).toBeNull();
  });
});

function run(state: RecordFormState, ...actions: RecordFormAction[]): RecordFormState {
  return actions.reduce(recordFormReducer, state);
}

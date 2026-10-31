import { defaultKeymap, history, historyKeymap } from '@codemirror/commands';
import { json } from '@codemirror/lang-json';
import { java } from '@codemirror/lang-java';
import { python } from '@codemirror/lang-python';
import {
  MariaSQL,
  MSSQL,
  MySQL,
  PLSQL,
  PostgreSQL,
  SQLite,
  StandardSQL,
  sql as sqlLanguage,
  type SQLDialect,
} from '@codemirror/lang-sql';
import { xml } from '@codemirror/lang-xml';
import { yaml } from '@codemirror/lang-yaml';
import { HighlightStyle, LanguageSupport, StreamLanguage, syntaxHighlighting } from '@codemirror/language';
import { powerShell } from '@codemirror/legacy-modes/mode/powershell';
import { shell } from '@codemirror/legacy-modes/mode/shell';
import { search, searchKeymap } from '@codemirror/search';
import { Annotation, Compartment, EditorState, Transaction } from '@codemirror/state';
import { drawSelection, EditorView, highlightSpecialChars, keymap, lineNumbers } from '@codemirror/view';
import { tags } from '@lezer/highlight';
import { type RefObject, useEffect, useRef, useState, useSyncExternalStore } from 'react';
import {
  formatDialect,
  mariadb,
  mysql,
  plsql,
  postgresql,
  sql as standardSql,
  sqlite,
  transactsql,
  type DialectOptions,
} from 'sql-formatter';

/*
 * The code of a `code` field (M6.9, design 8.3): CodeMirror 6 with the languages of the design, and sql-formatter for
 * "Format". `CodeBlock.tsx` shows it in the record view and `CodeEditor.tsx` edits it in the form, both through
 * `useCodeView`.
 */

/** The dialects of a SQL, by the `dialect` of its template: the names of sql-formatter, without regard to case. */
const sqlDialects = new Map<string, { highlight: SQLDialect; format: DialectOptions }>([
  ['plsql', { highlight: PLSQL, format: plsql }],
  ['postgresql', { highlight: PostgreSQL, format: postgresql }],
  ['mysql', { highlight: MySQL, format: mysql }],
  ['mariadb', { highlight: MariaSQL, format: mariadb }],
  ['sqlite', { highlight: SQLite, format: sqlite }],
  ['transactsql', { highlight: MSSQL, format: transactsql }],
  ['tsql', { highlight: MSSQL, format: transactsql }],
]);

// The legacy modes are defined once: each `StreamLanguage.define` is a language of its own.
const shellLanguage = StreamLanguage.define(shell);
const powerShellLanguage = StreamLanguage.define(powerShell);

/** The languages other than SQL, by the `language` of the template or the record, without regard to case. */
const languages = new Map<string, () => LanguageSupport>([
  ['bash', () => new LanguageSupport(shellLanguage)],
  ['sh', () => new LanguageSupport(shellLanguage)],
  ['shell', () => new LanguageSupport(shellLanguage)],
  ['powershell', () => new LanguageSupport(powerShellLanguage)],
  ['json', json],
  ['xml', xml],
  ['yaml', yaml],
  ['yml', yaml],
  ['java', java],
  ['python', python],
]);

/** Whether the code is a SQL, which "Format" formats. */
export function isSql(language: string | null): boolean {
  return language?.toLowerCase() === 'sql';
}

/**
 * The highlighting of a code: its language, and the dialect of a SQL, whose unknown dialect is standard SQL. Null for a
 * language that is not known, such as `text`, which shows as plain text.
 */
export function codeLanguage(language: string | null, dialect: string | null): LanguageSupport | null {
  if (isSql(language)) {
    return sqlLanguage({ dialect: sqlDialectOf(dialect)?.highlight ?? StandardSQL });
  }

  return languages.get(language?.toLowerCase() ?? '')?.() ?? null;
}

/** The dialect of sql-formatter for the `dialect` of a SQL: `plsql` for Oracle, and standard SQL for another. */
export function formatterDialect(dialect: string | null): DialectOptions {
  return sqlDialectOf(dialect)?.format ?? standardSql;
}

/**
 * A SQL formatted by its dialect. Standard SQL takes the bind variables of the design (`:contract`), which its dialect
 * of sql-formatter does not know. Throws an `Error` with sql-formatter's sentence when it cannot read the SQL.
 */
export function formatSql(code: string, dialect: string | null): string {
  const format = formatterDialect(dialect);
  return formatDialect(
    code,
    format === standardSql ? { dialect: format, paramTypes: { named: [':'] } } : { dialect: format },
  );
}

/** The first line of the sentence of a format that failed: sql-formatter adds a hint about the dialect under it. */
export function formatProblem(error: unknown): string {
  return error instanceof Error ? error.message.split('\n', 1)[0] : String(error);
}

function sqlDialectOf(dialect: string | null) {
  return sqlDialects.get(dialect?.toLowerCase() ?? '');
}

export type ResolvedTheme = 'light' | 'dark';

/** The theme that shows: the choice of `data-theme` on the root (theme.ts), or the system's without one. */
export function resolvedTheme(dataTheme: string | undefined, systemDark: boolean): ResolvedTheme {
  if (dataTheme === 'light' || dataTheme === 'dark') {
    return dataTheme;
  }

  return systemDark ? 'dark' : 'light';
}

const darkQuery = '(prefers-color-scheme: dark)';

function subscribeToTheme(onChange: () => void): () => void {
  const observer = new MutationObserver(onChange);
  observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
  const system = window.matchMedia(darkQuery);
  system.addEventListener('change', onChange);
  return () => {
    observer.disconnect();
    system.removeEventListener('change', onChange);
  };
}

function themeNow(): ResolvedTheme {
  return resolvedTheme(document.documentElement.dataset.theme, window.matchMedia(darkQuery).matches);
}

/** The resolved theme, also when the choice or the system's theme changes (kiler-ui, "Theme"). */
export function useResolvedTheme(): ResolvedTheme {
  return useSyncExternalStore(subscribeToTheme, themeNow);
}

/*
 * The colours are the variables of index.css and of Code.css, whose light-dark() follows the theme by CSS: they reach
 * into the shadow root, as custom properties and color-scheme inherit. The resolved theme picks the light or the dark
 * side of CodeMirror's own base theme, such as the selection and the buttons of the search.
 */
const kilerTheme = EditorView.theme({
  '&': {
    color: 'var(--color-text)',
    backgroundColor: 'var(--color-page)',
    border: '1px solid var(--color-border)',
    borderRadius: 'var(--radius)',
    fontSize: '0.9rem',
    maxHeight: '40rem',
  },
  '&.cm-focused': {
    outline: '2px solid var(--color-accent)',
    outlineOffset: '-1px',
  },
  '.cm-scroller': {
    overflow: 'auto',
    fontFamily: "Consolas, 'Cascadia Mono', monospace",
    lineHeight: '1.5',
  },
  '.cm-content': {
    caretColor: 'var(--color-text)',
  },
  '.cm-cursor, .cm-dropCursor': {
    borderLeftColor: 'var(--color-text)',
  },
  '.cm-gutters': {
    color: 'var(--color-muted)',
    backgroundColor: 'var(--color-surface)',
    borderRight: '1px solid var(--color-border)',
    borderRadius: 'var(--radius) 0 0 var(--radius)',
  },
  '.cm-panels': {
    color: 'var(--color-text)',
    backgroundColor: 'var(--color-surface)',
  },
  '.cm-panels-top': {
    borderBottom: '1px solid var(--color-border)',
  },
  '.cm-textfield': {
    color: 'var(--color-text)',
    backgroundColor: 'var(--color-page)',
    border: '1px solid var(--color-border)',
    borderRadius: 'var(--radius)',
  },
  '.cm-button, .cm-button:active': {
    color: 'var(--color-text)',
    backgroundColor: 'var(--color-surface)',
    backgroundImage: 'none',
    border: '1px solid var(--color-border)',
    borderRadius: 'var(--radius)',
  },
});

const kilerHighlight = HighlightStyle.define([
  { tag: tags.keyword, color: 'var(--code-keyword)' },
  { tag: [tags.string, tags.character, tags.regexp], color: 'var(--code-string)' },
  { tag: [tags.number, tags.bool, tags.null, tags.atom], color: 'var(--code-number)' },
  { tag: tags.comment, color: 'var(--code-comment)', fontStyle: 'italic' },
  { tag: [tags.typeName, tags.className, tags.namespace, tags.standard(tags.name)], color: 'var(--code-type)' },
  { tag: [tags.propertyName, tags.attributeName, tags.function(tags.variableName)], color: 'var(--code-property)' },
  { tag: [tags.tagName, tags.special(tags.name), tags.special(tags.variableName)], color: 'var(--code-name)' },
  { tag: [tags.meta, tags.processingInstruction], color: 'var(--code-comment)' },
  { tag: tags.invalid, color: 'var(--color-danger)' },
]);

/** A change of the code from its prop, not from the user: not undone, and not given back through `onChange`. */
const fromProp = Annotation.define<boolean>();

/** How the code takes the user: read it (the record view), edit it, or neither (a form that is sending). */
export type CodeAccess = 'read' | 'edit' | 'disabled';

/** The editor of a form has room for a few lines, also while it is empty. */
const formTheme = EditorView.theme({
  '.cm-content, .cm-gutter': { minHeight: '10rem' },
});

function accessExtension(access: CodeAccess) {
  // A code to read stays focusable, so the keyboard selects and searches it (Ctrl+F).
  return [
    EditorView.editable.of(access !== 'disabled'),
    EditorState.readOnly.of(access !== 'edit'),
    access === 'read' ? [] : formTheme,
  ];
}

/**
 * CodeMirror in a shadow root of `host`. CodeMirror writes its styles at run time: into the document it would need a
 * `<style>` element, which the Content Security Policy of Kiler refuses (kiler-ui, "The embedded build"), while into a
 * shadow root it adopts a constructed style sheet, which the policy does not govern. So the page's CSS does not reach
 * the editor either: its look is `kilerTheme` and `kilerHighlight`. A focus of `host` itself, such as `focus()` of its
 * id, goes on to the editor's text. The props change the editor in place; `onChange` gets each change by the user.
 */
export function useCodeView(
  host: RefObject<HTMLElement | null>,
  {
    code,
    language,
    dialect,
    access,
    onChange,
  }: {
    code: string;
    language: string | null;
    dialect: string | null;
    access: CodeAccess;
    onChange?: (code: string) => void;
  },
): RefObject<EditorView | null> {
  const theme = useResolvedTheme();
  const view = useRef<EditorView>(null);
  const [compartments] = useState(() => ({
    language: new Compartment(),
    theme: new Compartment(),
    access: new Compartment(),
  }));
  const changed = useRef(onChange);
  useEffect(() => {
    changed.current = onChange;
  });

  // The editor starts empty; the effects below give it the props, in this order, also after the second mount of
  // StrictMode, which runs every effect again.
  useEffect(() => {
    const element = host.current;
    if (element === null) {
      return;
    }

    // Not `delegatesFocus`: it gives the focus to the first element that takes one, CodeMirror's scroller.
    const root = element.shadowRoot ?? element.attachShadow({ mode: 'open' });
    const editor = new EditorView({
      parent: root,
      root,
      state: EditorState.create({
        extensions: [
          lineNumbers(),
          highlightSpecialChars(),
          history(),
          drawSelection(),
          search({ top: true }),
          keymap.of([...defaultKeymap, ...historyKeymap, ...searchKeymap]),
          syntaxHighlighting(kilerHighlight),
          kilerTheme,
          compartments.language.of([]),
          compartments.theme.of([]),
          compartments.access.of([]),
          EditorView.updateListener.of((update) => {
            if (update.docChanged && !update.transactions.some((transaction) => transaction.annotation(fromProp))) {
              changed.current?.(update.state.doc.toString());
            }
          }),
        ],
      }),
    });
    view.current = editor;
    // A focus inside the shadow root, such as the field of the search, reaches the host too, and stays where it is.
    const focus = () => {
      if (root.activeElement === null) {
        editor.focus();
      }
    };
    element.addEventListener('focus', focus);
    return () => {
      element.removeEventListener('focus', focus);
      editor.destroy();
      view.current = null;
    };
  }, [host, compartments]);

  useEffect(() => {
    const editor = view.current;
    if (editor !== null && editor.state.doc.toString() !== code) {
      editor.dispatch({
        changes: { from: 0, to: editor.state.doc.length, insert: code },
        annotations: [fromProp.of(true), Transaction.addToHistory.of(false)],
      });
    }
  }, [code]);

  useEffect(() => {
    view.current?.dispatch({ effects: compartments.language.reconfigure(codeLanguage(language, dialect) ?? []) });
  }, [compartments, language, dialect]);

  useEffect(() => {
    view.current?.dispatch({ effects: compartments.theme.reconfigure(EditorView.darkTheme.of(theme === 'dark')) });
  }, [compartments, theme]);

  useEffect(() => {
    view.current?.dispatch({ effects: compartments.access.reconfigure(accessExtension(access)) });
  }, [compartments, access]);

  return view;
}

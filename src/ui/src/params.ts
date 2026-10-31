/*
 * The parameters of a SQL (M6.15, design 8.3): its bind variables, such as `:contract`, and the query with values in
 * their place, for "Copy filled in". The record keeps its query as it is.
 */

/** A place of a bind variable in a SQL: its name without the colon, and where it is, the colon included. */
interface Bind {
  name: string;
  from: number;
  to: number;
}

const nameStart = /[A-Za-z_]/;
const namePart = /[A-Za-z0-9_$#]/;
const dollarQuote = /\$(?:[A-Za-z_][A-Za-z0-9_]*)?\$/y;

/** The closing delimiters of Oracle's quoting mechanism, `q'[…]'`; another delimiter closes itself. */
const closers: Readonly<Record<string, string>> = { '[': ']', '{': '}', '(': ')', '<': '>' };

/**
 * The bind variables of a SQL, in their order: a colon and a name that starts with a letter or `_`. Not in a string
 * (`'…'` with `''` in it, Oracle's `q'[…]'`, PostgreSQL's `$tag$…$tag$`), a quoted name (`"…"`, `` `…` ``) or a comment
 * (`--`, `/* … *\/`), and not a cast (`::`) or the assignment of PL/SQL (`:=`). One that is not closed runs to the end.
 */
function bindsOf(sql: string): Bind[] {
  const binds: Bind[] = [];
  let at = 0;
  while (at < sql.length) {
    const char = sql[at];
    const next = sql[at + 1] ?? '';
    const afterName = at > 0 && namePart.test(sql[at - 1]);
    if (char === '-' && next === '-') {
      at = endOf(sql, '\n', at + 2);
    } else if (char === '/' && next === '*') {
      at = endOf(sql, '*/', at + 2);
    } else if ((char === 'q' || char === 'Q') && next === "'" && !afterName && at + 2 < sql.length) {
      const open = sql[at + 2];
      at = endOf(sql, `${closers[open] ?? open}'`, at + 3);
    } else if (char === "'" || char === '"' || char === '`') {
      // A doubled quote in it, as in 'it''s', ends one text and starts the next, so it stays in a text.
      at = endOf(sql, char, at + 1);
    } else if (char === '$' && !afterName) {
      dollarQuote.lastIndex = at;
      const tag = dollarQuote.exec(sql)?.[0];
      at = tag === undefined ? at + 1 : endOf(sql, tag, at + tag.length);
    } else if (char === ':' && (next === ':' || next === '=')) {
      at += 2;
    } else if (char === ':' && nameStart.test(next)) {
      let to = at + 2;
      while (to < sql.length && namePart.test(sql[to])) {
        to++;
      }

      binds.push({ name: sql.slice(at + 1, to), from: at, to });
      at = to;
    } else {
      at++;
    }
  }

  return binds;
}

/** The place after `end`, searched from `from`, or the end of the SQL without it. */
function endOf(sql: string, end: string, from: number): number {
  const found = sql.indexOf(end, from);
  return found < 0 ? sql.length : found + end.length;
}

/**
 * The parameters of a SQL, each once, in the order of their first place, as that place spells them. A name is one
 * parameter without regard to case, as Oracle reads it.
 */
export function findParameters(sql: string): string[] {
  const names = new Map<string, string>();
  for (const { name } of bindsOf(sql)) {
    if (!names.has(name.toLowerCase())) {
      names.set(name.toLowerCase(), name);
    }
  }

  return [...names.values()];
}

/** The key of a parameter among the values of {@link fillParameters}. */
export function parameterKey(name: string): string {
  return name.toLowerCase();
}

/**
 * A value as SQL (decided in M6.15): a number goes in as it is, and any other value as a text in single quotes, a
 * quote in it doubled. A number has no leading zero, so `007`, a code rather than a number, stays a text. A value in
 * single quotes goes in as it is typed, so a number for a text column, such as `'1234567'`, stays a text there.
 */
export function sqlLiteral(value: string): string {
  if (/^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?$/.test(value) || /^'.*'$/s.test(value)) {
    return value;
  }

  return `'${value.replaceAll("'", "''")}'`;
}

/**
 * The SQL with each parameter that has a value, by {@link parameterKey}, in its places, by {@link sqlLiteral}. A value is
 * taken without the spaces around it, and a parameter without a value stays as it is.
 */
export function fillParameters(sql: string, values: ReadonlyMap<string, string>): string {
  let filled = '';
  let done = 0;
  for (const bind of bindsOf(sql)) {
    const value = values.get(parameterKey(bind.name))?.trim() ?? '';
    if (value !== '') {
      filled += sql.slice(done, bind.from) + sqlLiteral(value);
      done = bind.to;
    }
  }

  return filled + sql.slice(done);
}

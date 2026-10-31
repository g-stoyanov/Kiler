import { MSSQL, PLSQL, StandardSQL } from '@codemirror/lang-sql';
import { mariadb, plsql, sql, transactsql } from 'sql-formatter';
import { describe, expect, it } from 'vitest';
import { codeLanguage, formatProblem, formatSql, formatterDialect, isSql, resolvedTheme } from './code.ts';

describe('the language of a code', () => {
  it('gives a SQL its dialect, without regard to case', () => {
    expect(codeLanguage('sql', 'plsql')?.language).toBe(PLSQL.language);
    expect(codeLanguage('SQL', 'PLSQL')?.language).toBe(PLSQL.language);
    expect(codeLanguage('sql', 'tsql')?.language).toBe(MSSQL.language);
  });

  it('gives a SQL without a dialect, or with one that is not known, standard SQL', () => {
    expect(codeLanguage('sql', null)?.language).toBe(StandardSQL.language);
    expect(codeLanguage('sql', 'oracle')?.language).toBe(StandardSQL.language);
    expect(codeLanguage('sql', 'constructor')?.language).toBe(StandardSQL.language);
  });

  it.each([
    ['bash', 'shell'],
    ['sh', 'shell'],
    ['PowerShell', 'powershell'],
    ['json', 'json'],
    ['xml', 'xml'],
    ['yaml', 'yaml'],
    ['yml', 'yaml'],
    ['java', 'java'],
    ['python', 'python'],
  ])('gives %s its language', (language, name) => {
    expect(codeLanguage(language, null)?.language.name).toBe(name);
  });

  it('shows a language that is not known as plain text', () => {
    expect(codeLanguage('text', null)).toBeNull();
    expect(codeLanguage('cobol', 'plsql')).toBeNull();
    expect(codeLanguage('toString', null)).toBeNull();
    expect(codeLanguage(null, null)).toBeNull();
  });

  it('formats a SQL only', () => {
    expect(isSql('sql')).toBe(true);
    expect(isSql('Sql')).toBe(true);
    expect(isSql('bash')).toBe(false);
    expect(isSql(null)).toBe(false);
  });
});

describe('the format of a SQL', () => {
  it('takes the dialect of the formatter by the dialect of the SQL', () => {
    expect(formatterDialect('plsql')).toBe(plsql);
    expect(formatterDialect('PLSQL')).toBe(plsql);
    expect(formatterDialect('mariadb')).toBe(mariadb);
    expect(formatterDialect('tsql')).toBe(transactsql);
    expect(formatterDialect('transactsql')).toBe(transactsql);
  });

  it('takes standard SQL without a dialect, or for one that is not known', () => {
    expect(formatterDialect(null)).toBe(sql);
    expect(formatterDialect('oracle')).toBe(sql);
    expect(formatterDialect('constructor')).toBe(sql);
  });

  it('formats a short PL/SQL block', () => {
    const block = "begin update contracts set status = 'closed' where contract_no = :contract; commit; end;";

    expect(formatSql(block, 'plsql')).toBe(
      [
        'begin',
        'update contracts',
        'set',
        "  status = 'closed'",
        'where',
        '  contract_no = :contract;',
        '',
        'commit;',
        '',
        'end;',
      ].join('\n'),
    );
  });

  it('keeps the bind variables of standard SQL', () => {
    expect(formatSql('select a from t where x = :contract', null)).toBe(
      ['select', '  a', 'from', '  t', 'where', '  x = :contract'].join('\n'),
    );
  });

  it('throws on a SQL that it cannot read, with the first line of the sentence', () => {
    let error: unknown;
    try {
      formatSql('select "a from t', 'plsql');
    } catch (thrown) {
      error = thrown;
    }

    expect(error).toBeInstanceOf(Error);
    expect(formatProblem(error)).toBe('Parse error: Unexpected ""a from t" at line 1 column 8.');
  });
});

describe('the resolved theme', () => {
  it('takes the choice of the page', () => {
    expect(resolvedTheme('dark', false)).toBe('dark');
    expect(resolvedTheme('light', true)).toBe('light');
  });

  it("takes the system's theme without a choice", () => {
    expect(resolvedTheme(undefined, true)).toBe('dark');
    expect(resolvedTheme(undefined, false)).toBe('light');
    expect(resolvedTheme('other', true)).toBe('dark');
  });
});

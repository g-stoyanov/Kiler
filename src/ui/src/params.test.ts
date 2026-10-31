import { describe, expect, it } from 'vitest';
import { fillParameters, findParameters, parameterKey, sqlLiteral } from './params.ts';

describe('the parameters of a SQL', () => {
  it('finds the bind variables in their order, each once', () => {
    const sql = 'SELECT * FROM t WHERE contract_no = :contract AND created >= :from_date OR contract_no = :contract';

    expect(findParameters(sql)).toEqual(['contract', 'from_date']);
  });

  it('takes the names with digits, $ and #, and one name without regard to case', () => {
    expect(findParameters('WHERE a = :p1 AND b = :v$x AND c = :n#2 AND d = :P1')).toEqual(['p1', 'v$x', 'n#2']);
  });

  it('finds a name next to the signs around it', () => {
    expect(findParameters('WHERE a IN (:a,:b) AND c=:c;')).toEqual(['a', 'b', 'c']);
  });

  it('leaves out the colons in strings and quoted names', () => {
    const sql = `SELECT ':text', 'it''s :quoted', "col:name", \`my:col\` FROM t WHERE a = :real`;

    expect(findParameters(sql)).toEqual(['real']);
  });

  it("leaves out the colons in Oracle's q'…' and PostgreSQL's $…$ strings", () => {
    const sql = "SELECT q'[it's :q]', Q'{:q2}', q'!:q3!', $$ :d $$, $tag$ :t $tag$, v$session.x FROM t WHERE a = :real";

    expect(findParameters(sql)).toEqual(['real']);
  });

  it('leaves out the colons in comments', () => {
    const sql = ['SELECT a -- :line', 'FROM t /* :block', ':still */ WHERE a = :real'].join('\n');

    expect(findParameters(sql)).toEqual(['real']);
  });

  it('leaves out a cast and the assignment of PL/SQL', () => {
    expect(findParameters('SELECT x::int, y :: text FROM t; BEGIN v := 1; w:=:value; END;')).toEqual(['value']);
  });

  it('leaves out a colon without a name, and one before a digit', () => {
    expect(findParameters('SELECT a[1:2], b : c, :1 FROM t')).toEqual([]);
  });

  it('runs a string or a comment that is not closed to the end', () => {
    expect(findParameters("SELECT :a, 'open :b")).toEqual(['a']);
    expect(findParameters('SELECT :a /* open :b')).toEqual(['a']);
  });
});

describe('a value as SQL', () => {
  it.each(['0', '7', '-12', '1234567', '3.25', '-0.5'])('puts the number %s in as it is', (value) => {
    expect(sqlLiteral(value)).toBe(value);
  });

  it.each([
    ['007', "'007'"],
    ['1e5', "'1e5'"],
    ['1.', "'1.'"],
    ['ACME', "'ACME'"],
    ['2026-10-31', "'2026-10-31'"],
    ["O'Brien", "'O''Brien'"],
  ])('puts the text %s in quotes', (value, literal) => {
    expect(sqlLiteral(value)).toBe(literal);
  });

  it.each(["'1234567'", "'it''s'", "''"])('puts the value %s, typed in quotes, in as it is', (value) => {
    expect(sqlLiteral(value)).toBe(value);
  });

  it('quotes a value with a quote at one end only', () => {
    expect(sqlLiteral("'")).toBe("''''");
    expect(sqlLiteral("'abc")).toBe("'''abc'");
  });
});

describe('a query filled in', () => {
  const values = (entries: Record<string, string>) =>
    new Map(Object.entries(entries).map(([name, value]) => [parameterKey(name), value]));

  it('puts each value in every place of its parameter', () => {
    const sql = 'SELECT * FROM t WHERE contract_no = :contract AND owner = :owner OR parent = :Contract';

    expect(fillParameters(sql, values({ contract: '1234567', owner: "O'Brien" }))).toBe(
      "SELECT * FROM t WHERE contract_no = 1234567 AND owner = 'O''Brien' OR parent = 1234567",
    );
  });

  it('leaves a parameter without a value, or with spaces only, as it is', () => {
    expect(fillParameters('WHERE a = :a AND b = :b AND c = :c', values({ a: ' 5 ', b: '  ' }))).toBe(
      'WHERE a = 5 AND b = :b AND c = :c',
    );
  });

  it('leaves the strings, the comments and the casts as they are', () => {
    const sql = "SELECT ':a', x::int -- :a\nFROM t WHERE a = :a";

    expect(fillParameters(sql, values({ a: 'x' }))).toBe("SELECT ':a', x::int -- :a\nFROM t WHERE a = 'x'");
  });

  it('gives a SQL without parameters as it is', () => {
    expect(fillParameters('SELECT 1 FROM dual', values({ a: '1' }))).toBe('SELECT 1 FROM dual');
  });
});

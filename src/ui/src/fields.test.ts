import { describe, expect, it } from 'vitest';
import { isCodeValue, isRefValue, localDateTime, recordPath, urlView, valueText } from './fields.ts';

describe('valueText', () => {
  it.each([
    ['a text', 'db01.example.test', 'db01.example.test'],
    ['a text of several lines', 'First line\nSecond line', 'First line\nSecond line'],
    ['a date, as it is', '2026-10-21', '2026-10-21'],
    ['a whole number', 1521, '1521'],
    ['a number with a fraction', 2.5, '2.5'],
    ['a negative number', -3, '-3'],
    ['a code without a language', 'SELECT 1 FROM dual', 'SELECT 1 FROM dual'],
    ['the code of a code with its language', { language: 'bash', code: 'ls -la' }, 'ls -la'],
    ['the name of the record of a ref', { id: 'k3x9q2mf', name: 'Payments database' }, 'Payments database'],
  ])('gives %s', (_name, value, text) => {
    expect(valueText(value)).toBe(text);
  });

  it('gives no text for a ref whose record has gone', () => {
    expect(valueText({ id: 'k3x9q2mf', name: null })).toBeNull();
  });
});

describe('the shape of a value', () => {
  it('tells a code with its language from a ref and from a text', () => {
    expect(isCodeValue({ language: 'sql', code: 'SELECT 1' })).toBe(true);
    expect(isCodeValue({ id: 'k3x9q2mf', name: null })).toBe(false);
    expect(isCodeValue('SELECT 1')).toBe(false);
    expect(isCodeValue(7)).toBe(false);
  });

  it('tells a ref from a code with its language and from a text', () => {
    expect(isRefValue({ id: 'k3x9q2mf', name: null })).toBe(true);
    expect(isRefValue({ language: 'sql', code: 'SELECT 1' })).toBe(false);
    expect(isRefValue('k3x9q2mf')).toBe(false);
  });
});

describe('recordPath', () => {
  it('gives the address of a record', () => {
    expect(recordPath('k3x9q2mf')).toBe('/records/k3x9q2mf');
  });

  it('keeps a slash, a question mark and a hash of an id in its segment', () => {
    expect(recordPath('a/b?c#d')).toBe('/records/a%2Fb%3Fc%23d');
  });
});

describe('urlView', () => {
  it.each([
    'http://wiki.example.test/payments',
    'https://wiki.example.test:8443/a?b=c#d',
    'HTTPS://Wiki.Example.Test/',
  ])('makes a link of %s', (text) => {
    expect(urlView(text)).toEqual({ kind: 'link', href: new URL(text).href });
  });

  it.each([
    'ftp://files.example.test/a',
    'file:///C:/Windows/notepad.exe',
    'javascript:alert(1)',
    'data:text/html,<b>x</b>',
    'mailto:team@example.test',
    '\\\\files.example.test\\share',
    'wiki.example.test/payments',
    '',
  ])('keeps %s as text', (text) => {
    expect(urlView(text)).toEqual({ kind: 'text' });
  });

  it.each([
    'http://localhost:5000/',
    'https://LOCALHOST/',
    'http://localhost./',
    'http://kiler.localhost:5000/',
    'http://127.0.0.1:5000/api/session',
    'http://127.12.34.56/',
    'http://127.1:5000/',
    'http://0x7f.0.0.1/',
    'http://2130706433/',
    'http://127.0.0.1./',
    'http://[::1]:5000/',
    'http://[0:0:0:0:0:0:0:1]/',
    'http://[::ffff:127.0.0.1]/',
  ])('keeps %s, an address of this computer, as text with "Copy"', (text) => {
    expect(urlView(text)).toEqual({ kind: 'local' });
  });

  it.each([
    'http://128.0.0.1/',
    'http://126.255.255.255/',
    'http://127.example.test/',
    'http://localhost.example.test/',
    'http://notlocalhost/',
    'http://[::2]/',
    'http://[::ffff:128.0.0.1]/',
  ])('makes a link of %s, which is not this computer', (text) => {
    expect(urlView(text).kind).toBe('link');
  });
});

describe('localDateTime', () => {
  it('shows a time of Kiler in the local zone', () => {
    expect(localDateTime('2026-10-21T11:32:00Z', 'Europe/Sofia')).toBe('2026-10-21 14:32');
    expect(localDateTime('2026-10-21T11:32:00Z', 'UTC')).toBe('2026-10-21 11:32');
  });

  it('moves the date with the zone', () => {
    expect(localDateTime('2026-10-21T22:30:00Z', 'Europe/Sofia')).toBe('2026-10-22 01:30');
    expect(localDateTime('2026-10-21T02:00:00Z', 'America/New_York')).toBe('2026-10-20 22:00');
  });

  it('follows the summer time of the zone', () => {
    expect(localDateTime('2026-01-15T11:32:00Z', 'Europe/Sofia')).toBe('2026-01-15 13:32');
  });

  it('gives midnight as 00, not 24', () => {
    expect(localDateTime('2026-10-20T21:00:00Z', 'Europe/Sofia')).toBe('2026-10-21 00:00');
  });
});

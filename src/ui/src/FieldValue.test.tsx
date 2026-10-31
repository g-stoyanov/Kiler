// @vitest-environment jsdom
import { act, type ReactNode } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeAll, describe, expect, it } from 'vitest';
import type { FieldType, RecordField, RecordValue } from './api.ts';
import { FieldValue } from './FieldValue.tsx';
import { texts } from './texts.ts';

let root: Root | undefined;
let container: HTMLElement | undefined;

beforeAll(() => {
  // React's act() warns outside a test environment that it knows.
  (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
});

afterEach(() => {
  act(() => root?.unmount());
  container?.remove();
  root = undefined;
  container = undefined;
});

function render(element: ReactNode): HTMLElement {
  container = document.createElement('div');
  document.body.append(container);
  root = createRoot(container);
  act(() => root?.render(<MemoryRouter>{element}</MemoryRouter>));
  return container;
}

function valueField(value: RecordValue): RecordField {
  return { key: 'host', level: 1, levelName: null, kind: 'value', value };
}

function show(field: RecordField, type?: FieldType, shorten?: (text: string) => string): HTMLElement {
  return render(<FieldValue field={field} type={type} shorten={shorten} />);
}

describe('FieldValue', () => {
  it('shows a text', () => {
    const shown = show(valueField('db01.example.test'), 'text');

    expect(shown.textContent).toBe('db01.example.test');
    expect(shown.querySelector('a')).toBeNull();
  });

  it('keeps the lines of a text of several lines', () => {
    const shown = show(valueField('First line\nSecond line'), 'multiline');

    expect(shown.querySelector('.field-lines')?.textContent).toBe('First line\nSecond line');
  });

  it('shows a number', () => {
    expect(show(valueField(1521), 'number').textContent).toBe('1521');
  });

  it('shows a code, with its language or without it, as code', () => {
    expect(show(valueField({ language: 'bash', code: 'ls -la' })).querySelector('code')?.textContent).toBe('ls -la');
    expect(show(valueField('SELECT 1'), 'code').querySelector('code')?.textContent).toBe('SELECT 1');
  });

  it('makes a ref a link to its record, with a slash and a question mark of its id in its segment', () => {
    const link = show(valueField({ id: 'a/b?c', name: 'Payments database' }), 'ref').querySelector('a');

    expect(link?.textContent).toBe('Payments database');
    expect(link?.getAttribute('href')).toBe('/records/a%2Fb%3Fc');
  });

  it('says that the record of a ref has gone, without a link', () => {
    const shown = show(valueField({ id: 'k3x9q2mf', name: null }), 'ref');

    expect(shown.textContent).toBe(texts.field.gone);
    expect(shown.querySelector('a')).toBeNull();
  });

  it('opens an http or https url in a new tab, without the opener and the referrer', () => {
    const link = show(valueField('https://wiki.example.test/payments'), 'url').querySelector('a');

    expect(link?.getAttribute('href')).toBe('https://wiki.example.test/payments');
    expect(link?.getAttribute('target')).toBe('_blank');
    expect(link?.getAttribute('rel')).toBe('noopener noreferrer');
  });

  it('shows a url of this computer as text, with "Copy" and why', () => {
    const shown = show(valueField('http://127.0.0.1:5000/'), 'url');

    expect(shown.querySelector('a')).toBeNull();
    expect(shown.textContent).toContain('http://127.0.0.1:5000/');
    expect(shown.querySelector('button')?.textContent).toBe(texts.copy.copy);
    expect(shown.textContent).toContain(texts.field.local);
  });

  it('shows a url of another scheme as text', () => {
    const shown = show(valueField('javascript:alert(1)'), 'url');

    expect(shown.querySelector('a')).toBeNull();
    expect(shown.querySelector('button')).toBeNull();
    expect(shown.textContent).toBe('javascript:alert(1)');
  });

  it('shows a text that looks like an address as text when the template does not say url', () => {
    expect(show(valueField('https://wiki.example.test/'), 'text').querySelector('a')).toBeNull();
    expect(show(valueField('https://wiki.example.test/')).querySelector('a')).toBeNull();
  });

  it('shortens what shows, and keeps the whole address in the link', () => {
    const link = show(valueField('https://wiki.example.test/payments'), 'url', (text) =>
      text.slice(0, 8),
    ).querySelector('a');

    expect(link?.textContent).toBe('https://');
    expect(link?.getAttribute('href')).toBe('https://wiki.example.test/payments');
  });

  it('says that an empty field has no value', () => {
    const shown = show({ key: 'tns', level: 1, levelName: null, kind: 'empty', value: null }, 'text');

    expect(shown.textContent).toBe(texts.field.empty);
  });

  it('shows a secret as dots, without its value', () => {
    const shown = show({ key: 'password', level: 3, levelName: null, kind: 'secret', value: null }, 'secret');

    expect(shown.textContent).toBe('••••');
  });

  it('shows the padlock with the name of the level', () => {
    const shown = show({ key: 'user', level: 3, levelName: 'Prod', kind: 'locked', value: null }, 'text');

    expect(shown.textContent).toBe('🔒 requires level Prod');
    expect(shown.querySelector('button')).toBeNull();
  });

  it('shows the padlock with the number of the level when levels.json cannot be read', () => {
    const shown = show({ key: 'user', level: 3, levelName: null, kind: 'locked', value: null }, 'text');

    expect(shown.textContent).toBe('🔒 requires level 3');
  });

  it('says that a field cannot be read', () => {
    const shown = show({ key: 'host', level: 1, levelName: null, kind: 'unreadable', value: null }, 'text');

    expect(shown.textContent).toBe(texts.field.unreadable);
  });
});

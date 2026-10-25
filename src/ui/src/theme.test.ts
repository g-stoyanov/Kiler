// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { applyThemeChoice, readThemeChoice, saveThemeChoice, themeStorageKey, type ThemeChoice } from './theme.ts';

/** A storage that the browser blocks: every call throws. */
const blocked = {
  getItem: () => {
    throw new DOMException('Blocked', 'SecurityError');
  },
  setItem: () => {
    throw new DOMException('Full', 'QuotaExceededError');
  },
} as unknown as Storage;

afterEach(() => {
  localStorage.clear();
  delete document.documentElement.dataset.theme;
});

describe('readThemeChoice', () => {
  it('is "system" when nothing is saved', () => {
    expect(readThemeChoice()).toBe('system');
  });

  it.each<ThemeChoice>(['light', 'dark', 'system'])('reads a saved "%s"', (choice) => {
    localStorage.setItem(themeStorageKey, choice);

    expect(readThemeChoice()).toBe(choice);
  });

  it.each(['', 'Dark', 'blue', 'dark '])('is "system" for a saved value that is not a choice: "%s"', (value) => {
    localStorage.setItem(themeStorageKey, value);

    expect(readThemeChoice()).toBe('system');
  });

  it('is "system" when the storage cannot be read', () => {
    expect(readThemeChoice(blocked)).toBe('system');
  });
});

describe('saveThemeChoice', () => {
  it('keeps the choice for the next page', () => {
    saveThemeChoice('dark');

    expect(localStorage.getItem(themeStorageKey)).toBe('dark');
    expect(readThemeChoice()).toBe('dark');
  });

  it('does not throw when the storage refuses', () => {
    expect(() => saveThemeChoice('light', blocked)).not.toThrow();
  });
});

describe('applyThemeChoice', () => {
  const root = () => document.documentElement;

  it.each<ThemeChoice>(['light', 'dark'])('sets data-theme to "%s"', (choice) => {
    applyThemeChoice(choice);

    expect(root().dataset.theme).toBe(choice);
  });

  it('removes data-theme for "system", so the CSS follows the system', () => {
    applyThemeChoice('dark');

    applyThemeChoice('system');

    expect(root().hasAttribute('data-theme')).toBe(false);
  });

  it('switches between light and dark', () => {
    applyThemeChoice('light');
    applyThemeChoice('dark');

    expect(root().dataset.theme).toBe('dark');
  });
});

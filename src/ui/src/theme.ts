/**
 * Light, dark or system theme. The CSS does the work: `color-scheme` on the root picks one side of every
 * `light-dark()` colour (index.css). Without `data-theme` the root has `color-scheme: light dark`, so "system" follows
 * Windows by itself, also before the page script runs and when Windows changes its theme.
 *
 * The choice is kept in `localStorage` until M6 moves it to the personal configuration.
 */
export type ThemeChoice = 'light' | 'dark' | 'system';

export const themeChoices: readonly ThemeChoice[] = ['light', 'dark', 'system'];

export const themeStorageKey = 'kiler.theme';

/** The saved choice, or "system" when there is none, it is not valid, or the storage cannot be read. */
export function readThemeChoice(storage: Storage | undefined = localStorageOrNothing()): ThemeChoice {
  try {
    const saved = storage?.getItem(themeStorageKey);
    return themeChoices.find((choice) => choice === saved) ?? 'system';
  } catch {
    return 'system';
  }
}

/** Saves the choice. A storage that refuses only means the choice is not remembered. */
export function saveThemeChoice(choice: ThemeChoice, storage: Storage | undefined = localStorageOrNothing()): void {
  try {
    storage?.setItem(themeStorageKey, choice);
  } catch {
    // Private mode or a full storage: the theme still changes for this page.
  }
}

export function applyThemeChoice(choice: ThemeChoice, root: HTMLElement = document.documentElement): void {
  if (choice === 'system') {
    delete root.dataset.theme;
  } else {
    root.dataset.theme = choice;
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

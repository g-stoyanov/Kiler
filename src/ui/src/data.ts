import { missingFile, type DataInfo } from './api.ts';

/**
 * The wizard runs in `missing`, and in `open` while `levels.json` and `settings.json` are both missing (M4.3), which
 * this says. A file that is there but does not fit is damage that an admin restores, never a new Kiler. Offline, the
 * wizard cannot write.
 */
export function isEmptyCopy(data: DataInfo): boolean {
  return data.state === 'open' && data.levels === missingFile && data.settings === missingFile;
}

/** Whether the routes show: the local copy is open or offline, and its `levels.json` can be used. */
export function showsRoutes(data: DataInfo): boolean {
  return (data.state === 'open' || data.state === 'offline') && data.levels === null;
}

/**
 * When to read the state again, in milliseconds; undefined for a state that changes only with a new start, and while
 * the wizard shows, so a new state does not take its recovery key away.
 */
export function nextRead(data: DataInfo): number | undefined {
  if (data.state === 'opening') {
    return 1000;
  }

  return data.state === 'offline' || (data.state === 'open' && !isEmptyCopy(data)) ? 10_000 : undefined;
}

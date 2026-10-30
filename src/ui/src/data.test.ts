import { describe, expect, it } from 'vitest';
import { dataStates, missingFile, type DataInfo, type DataState } from './api.ts';
import { isEmptyCopy, nextRead, showsRoutes } from './data.ts';

function data(state: DataState, levels: string | null = null, settings: string | null = null): DataInfo {
  return { state, folder: 'D:\\Demo\\data', levels, settings };
}

const damaged = 'The document does not fit.';

describe('isEmptyCopy', () => {
  it('is an open copy with neither levels.json nor settings.json, where the wizard runs', () => {
    expect(isEmptyCopy(data('open', missingFile, missingFile))).toBe(true);
  });

  it.each([
    ['offline, as the wizard cannot write', data('offline', missingFile, missingFile)],
    ['with levels.json', data('open', null, missingFile)],
    ['with settings.json', data('open', missingFile, null)],
    ['with a levels.json that does not fit, which an admin restores', data('open', damaged, missingFile)],
  ])('is not a copy %s', (_, info) => {
    expect(isEmptyCopy(info)).toBe(false);
  });
});

describe('showsRoutes', () => {
  it.each(['open', 'offline'] as const)('shows them in %s with a levels.json that fits', (state) => {
    expect(showsRoutes(data(state, null, missingFile))).toBe(true);
  });

  it('shows none without a levels.json that fits', () => {
    expect(showsRoutes(data('open', missingFile, missingFile))).toBe(false);
    expect(showsRoutes(data('offline', damaged))).toBe(false);
  });

  it.each(dataStates.filter((state) => state !== 'open' && state !== 'offline'))('shows none in %s', (state) => {
    expect(showsRoutes(data(state))).toBe(false);
  });
});

describe('nextRead', () => {
  it('reads again every second while the copy opens', () => {
    expect(nextRead(data('opening'))).toBe(1000);
  });

  it.each([
    ['open', data('open')],
    ['offline', data('offline')],
    ['offline on a first start', data('offline', missingFile, missingFile)],
    ['open with a damaged levels.json', data('open', damaged)],
  ])('reads again every 10 s while %s', (_, info) => {
    expect(nextRead(info)).toBe(10_000);
  });

  it.each([
    ['the data folder is missing', data('missing')],
    ['the copy is empty', data('open', missingFile, missingFile)],
  ])('does not read again while the wizard shows: %s', (_, info) => {
    expect(nextRead(info)).toBeUndefined();
  });

  it.each(['noFolder', 'unusable', 'inUse', 'indexUnreadable', 'failed'] as const)(
    'does not read again in %s, which only a new start changes',
    (state) => {
      expect(nextRead(data(state))).toBeUndefined();
    },
  );
});

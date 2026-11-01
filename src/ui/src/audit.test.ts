import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import type { AuditEntry } from './api.ts';
import { auditRows, dayBounds, dayOf, localTime, shiftDay } from './audit.ts';

// The local days are those of a zone with a change of the clock, whatever the zone of the machine: Node takes a new TZ
// of `process.env` at once, also on Windows, and Vitest runs each test file in a process of its own (its pool `forks`).
beforeAll(() => {
  vi.stubEnv('TZ', 'Europe/Sofia');
});

afterAll(() => {
  vi.unstubAllEnvs();
});

function hours(bounds: { from: string; to: string } | undefined): number {
  return bounds === undefined ? Number.NaN : (Date.parse(bounds.to) - Date.parse(bounds.from)) / 3_600_000;
}

describe('the bounds of a local day', () => {
  it('runs from the start of the day to the start of the next, in UTC', () => {
    expect(dayBounds('2026-11-01')).toEqual({ from: '2026-10-31T22:00:00.000Z', to: '2026-11-01T22:00:00.000Z' });
  });

  it('has 23 hours on the day that the clock goes forward', () => {
    const bounds = dayBounds('2026-03-29');

    expect(bounds).toEqual({ from: '2026-03-28T22:00:00.000Z', to: '2026-03-29T21:00:00.000Z' });
    expect(hours(bounds)).toBe(23);
  });

  it('has 25 hours on the day that the clock goes back', () => {
    const bounds = dayBounds('2026-10-25');

    expect(bounds).toEqual({ from: '2026-10-24T21:00:00.000Z', to: '2026-10-25T22:00:00.000Z' });
    expect(hours(bounds)).toBe(25);
  });

  it.each([[''], ['2026-02-30'], ['2026-13-01'], ['2026-1-01'], ['0000-01-01'], ['2026-11-01T00:00']])(
    'is not there for %j, which is not a day of the date field',
    (day) => {
      expect(dayBounds(day)).toBeUndefined();
    },
  );

  it('takes a year below 100 as it is', () => {
    expect(dayBounds('0099-06-15')?.from.slice(0, 10)).toBe('0099-06-14');
  });
});

describe('the day of a time', () => {
  it('is the local date, as the date field gives it', () => {
    expect(dayOf(new Date('2026-10-31T21:59:59Z'))).toBe('2026-10-31');
    expect(dayOf(new Date('2026-10-31T22:00:00Z'))).toBe('2026-11-01');
  });
});

describe('"Previous" and "Next"', () => {
  it.each([
    ['2026-03-29', -1, '2026-03-28'],
    ['2026-03-29', 1, '2026-03-30'],
    ['2026-10-25', -1, '2026-10-24'],
    ['2026-10-25', 1, '2026-10-26'],
    ['2026-12-31', 1, '2027-01-01'],
    ['2028-03-01', -1, '2028-02-29'],
  ])('go from %s by %i day to %s', (day, days, shifted) => {
    expect(shiftDay(day, days)).toBe(shifted);
  });

  it('stay at the first and the last day that the date field gives', () => {
    expect(shiftDay('0001-01-01', -1)).toBe('0001-01-01');
    expect(shiftDay('9999-12-31', 1)).toBe('9999-12-31');
  });
});

describe('the rows', () => {
  const entry: AuditEntry = {
    at: '2026-10-31T22:30:05Z',
    account: 'demo.admin',
    record: 'k3x9q2mf',
    name: 'Payments production database',
    field: 'password',
    use: 'copy',
    level: 3,
  };

  it('give the local time, the record as a link with its name, and the use', () => {
    expect(auditRows([entry, { ...entry, at: '2026-10-31T22:00:00Z', use: 'show' }])).toEqual([
      {
        key: '0',
        time: '00:30:05',
        account: 'demo.admin',
        name: 'Payments production database',
        link: '/records/k3x9q2mf',
        field: 'password',
        use: 'Copy',
      },
      {
        key: '1',
        time: '00:00:00',
        account: 'demo.admin',
        name: 'Payments production database',
        link: '/records/k3x9q2mf',
        field: 'password',
        use: 'Show',
      },
    ]);
  });

  it('give the time in another zone when it is named', () => {
    expect(localTime(entry.at, 'UTC')).toBe('22:30:05');
  });
});

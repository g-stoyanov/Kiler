import type { AuditEntry } from './api.ts';
import { recordPath } from './fields.ts';
import { texts } from './texts.ts';

/** A row of the audit screen: one view of a `secret` of the top level. */
export interface AuditRow {
  key: string;
  /** The time of the view, `14:32:05` in the browser's zone. */
  time: string;
  account: string;
  /** The name of the record at the view, and the address of the record, which a record deleted since gives as 404. */
  name: string;
  link: string;
  field: string;
  use: string;
}

/** A local day as the date field gives it, `2026-10-31`, the years 1 to 9999. */
const dayPattern = /^(\d{4})-(\d{2})-(\d{2})$/;

/** The day of a time in the browser's zone, as the date field gives it. */
export function dayOf(date: Date): string {
  const year = String(date.getFullYear()).padStart(4, '0');
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

/** The start of `day` with `days` added, in the browser's zone; undefined when `day` is not a day of the date field. */
function startOf(day: string, days = 0): Date | undefined {
  const match = dayPattern.exec(day);
  if (match === null) {
    return undefined;
  }

  const year = Number(match[1]);
  const month = Number(match[2]);
  const date = Number(match[3]);
  // `setFullYear`, as the constructor takes a year below 100 as 1900 and more. A start that the clock skips, as where it
  // goes forward at midnight, is the first time of the day that the clock shows.
  const start = new Date(0);
  start.setFullYear(year, month - 1, date);
  start.setHours(0, 0, 0, 0);
  if (year === 0 || start.getFullYear() !== year || start.getMonth() !== month - 1 || start.getDate() !== date) {
    return undefined;
  }

  if (days !== 0) {
    start.setDate(start.getDate() + days);
    start.setHours(0, 0, 0, 0);
  }

  return start;
}

/**
 * The span of a local day in UTC, as `GET /api/admin/audit` takes it: from the start of the day to the start of the next,
 * so a day on which the clock changes has 23 or 25 hours. Undefined when `day` is not a day of the date field, such as
 * the empty value of a date field that is being typed.
 */
export function dayBounds(day: string): { from: string; to: string } | undefined {
  const from = startOf(day);
  const to = startOf(day, 1);
  return from === undefined || to === undefined ? undefined : { from: from.toISOString(), to: to.toISOString() };
}

/** "Previous" (-1) and "Next" (1): the day `days` from `day`; `day` itself when either is not a day of the date field. */
export function shiftDay(day: string, days: number): string {
  const start = startOf(day, days);
  const shifted = start === undefined ? day : dayOf(start);
  return dayBounds(shifted) === undefined ? day : shifted;
}

/** The time of a time of Kiler in the browser's zone (or `timeZone`), `14:32:05`, as the audit screen shows one day. */
export function localTime(iso: string, timeZone?: string): string {
  const format = new Intl.DateTimeFormat('en', {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hourCycle: 'h23',
    timeZone,
  });
  const part = Object.fromEntries(format.formatToParts(new Date(iso)).map(({ type, value }) => [type, value]));
  return `${part.hour}:${part.minute}:${part.second}`;
}

/** The rows of the entries, in their order, the newest first. */
export function auditRows(entries: AuditEntry[], timeZone?: string): AuditRow[] {
  return entries.map((entry, index) => ({
    key: String(index),
    time: localTime(entry.at, timeZone),
    account: entry.account,
    name: entry.name,
    link: recordPath(entry.record),
    field: entry.field,
    use: texts.audit.uses[entry.use],
  }));
}

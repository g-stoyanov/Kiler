import type { CodeValue, RecordValue, RefValue } from './api.ts';

/*
 * The values of the fields for the screens of the records (M6.2). A value is told by its shape, not by the type of its
 * template, as a template changed since its records can give another shape (docs/api.md, "Values by type").
 */

/**
 * The address of a record. Its id is any text of the share, as a `ref` holds it, so a `/`, `?` or `#` in it stays in
 * its segment (M6.1 code review). `encodeURIComponent` keeps dots, so an id of `.` or `..` goes to the main view; a
 * `ref` with such an id has no record, so it shows as gone, without a link.
 */
export function recordPath(id: string): string {
  return `/records/${encodeURIComponent(id)}`;
}

/** A `code` whose template gives `languages`: the code with its language. */
export function isCodeValue(value: RecordValue): value is CodeValue {
  return typeof value === 'object' && 'code' in value;
}

/** A `ref`: the record that it points to, whose name is null when that record has gone. */
export function isRefValue(value: RecordValue): value is RefValue {
  return typeof value === 'object' && 'id' in value;
}

/**
 * The text of a value, as the search and the filters read it (docs/api.md, "The text of a value"): a text as it is, a
 * number as JSON writes it, the code of a `code` without its language, and the name of the record of a `ref`, which is
 * null when that record has gone. A `date` is a text, `2026-10-21`, without a time or a zone, so it shows as it is.
 */
export function valueText(value: RecordValue): string | null {
  if (typeof value === 'string') {
    return value;
  }

  if (typeof value === 'number') {
    return JSON.stringify(value);
  }

  return isCodeValue(value) ? value.code : value.name;
}

/**
 * What the page makes of a `url` (decided at the start of M6): a link only for `http` and `https`, as anyone can write
 * the share; `local` for an address of this computer, which shows as text with "Copy", as the browser sends the cookie
 * of Kiler to every port of 127.0.0.1 (the M1.4 risk); and any other text as it is.
 */
export type UrlView = { kind: 'link'; href: string } | { kind: 'local' } | { kind: 'text' };

export function urlView(text: string): UrlView {
  let url: URL;
  try {
    url = new URL(text);
  } catch {
    return { kind: 'text' };
  }

  if (url.protocol !== 'http:' && url.protocol !== 'https:') {
    return { kind: 'text' };
  }

  return isLocalHost(url.hostname) ? { kind: 'local' } : { kind: 'link', href: url.href };
}

/**
 * `localhost`, a name under `.localhost`, an address of 127.0.0.0/8, also as IPv6 (`::ffff:127.x.x.x`), and `::1`. The
 * parser of `URL` gives the host in lowercase and an IPv4 address in its four decimal parts, whatever form it was
 * written in (`127.1`, `0x7f.0.0.1`, `2130706433`); a dot at the end of a name still names the same host.
 */
function isLocalHost(hostname: string): boolean {
  const host = hostname.replace(/\.+$/, '');
  return (
    host === 'localhost' ||
    host.endsWith('.localhost') ||
    /^127\.\d+\.\d+\.\d+$/.test(host) ||
    host === '[::1]' ||
    /^\[::ffff:7f[0-9a-f]{2}:[0-9a-f]{1,4}\]$/.test(host)
  );
}

/**
 * A time of Kiler, UTC in ISO 8601 (`updatedAt`, `createdAt`), in local time as `2026-10-21 14:32` (design 6.1). The
 * zone is that of the browser; a test gives its own.
 */
export function localDateTime(iso: string, timeZone?: string): string {
  const format = new Intl.DateTimeFormat('en', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
    timeZone,
  });
  const part = Object.fromEntries(format.formatToParts(new Date(iso)).map(({ type, value }) => [type, value]));
  return `${part.year}-${part.month}-${part.day} ${part.hour}:${part.minute}`;
}

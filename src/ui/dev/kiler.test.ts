import { describe, expect, it } from 'vitest';
import { maskToken, parseOpenLine } from './kiler.ts';

/** A made-up token in the form of `AccessToken`: 43 characters of base64url. */
const token = 'Ab3_-Ab3_-Ab3_-Ab3_-Ab3_-Ab3_-Ab3_-Ab3_-Ab3';

describe('parseOpenLine', () => {
  it('reads the address and the token of Kiler', () => {
    const open = parseOpenLine(`Open: http://127.0.0.1:5050/?t=${token}`);

    expect(open?.server.href).toBe('http://127.0.0.1:5050/');
    expect(open?.token).toBe(token);
  });

  it.each([
    [
      'another line',
      `Kiler is listening on http://127.0.0.1:5050. It stops when its last page closes, or with Ctrl+C.`,
    ],
    ['no address', 'Open: '],
    ['not an address', 'Open: not an address'],
    ['localhost', `Open: http://localhost:5050/?t=${token}`],
    ['another host', `Open: http://kiler.example:5050/?t=${token}`],
    ['https', `Open: https://127.0.0.1:5050/?t=${token}`],
    ['no port', `Open: http://127.0.0.1/?t=${token}`],
    ['another path', `Open: http://127.0.0.1:5050/api/health?t=${token}`],
    ['a user', `Open: http://someone@127.0.0.1:5050/?t=${token}`],
    ['a fragment', `Open: http://127.0.0.1:5050/?t=${token}#x`],
    ['another parameter', `Open: http://127.0.0.1:5050/?t=${token}&x=1`],
    ['the token twice', `Open: http://127.0.0.1:5050/?t=${token}&t=${token}`],
    ['no token', 'Open: http://127.0.0.1:5050/'],
    ['a short token', `Open: http://127.0.0.1:5050/?t=${token.slice(1)}`],
    ['a long token', `Open: http://127.0.0.1:5050/?t=${token}A`],
    ['a token with another character', `Open: http://127.0.0.1:5050/?t=${token.slice(1)}.`],
  ])('gives null for %s', (_, line) => {
    expect(parseOpenLine(line)).toBeNull();
  });
});

describe('maskToken', () => {
  it('hides the token of an Open line', () => {
    const masked = maskToken(`Open: http://127.0.0.1:5050/?t=${token}`);

    expect(masked).toBe('Open: http://127.0.0.1:5050/?t=…');
    expect(masked).not.toContain(token);
  });

  it('hides the token in any text, such as a log line of Vite', () => {
    expect(maskToken(`http proxy error: /?t=${token}`)).toBe('http proxy error: /?t=…');
    expect(maskToken(`/x?a=1&t=${token}&b=2 /?t=${token}#top`)).toBe('/x?a=1&t=…&b=2 /?t=…#top');
  });

  it('leaves the other parameters and lines as they are', () => {
    expect(maskToken('/src/events-worker.ts?worker_file&type=module')).toBe(
      '/src/events-worker.ts?worker_file&type=module',
    );
  });

  it('leaves other lines as they are', () => {
    const line = 'Kiler is listening on http://127.0.0.1:5050.';

    expect(maskToken(line)).toBe(line);
  });
});

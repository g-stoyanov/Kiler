import http from 'node:http';
import type { AddressInfo } from 'node:net';
import type { ViteDevServer } from 'vite';
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { devServerAddress, startDevServer } from './vite-server.ts';

/** What the made-up Kiler saw of one request. */
interface Seen {
  method: string;
  url: string;
  host: string | undefined;
  fetchSite: string | undefined;
  cookie: string | undefined;
}

/** The answer of the made-up Kiler to the token exchange, as AccessGuard sends it. */
const exchangeCookie = 'kiler-0123456789abcdef=made-up-token; path=/; samesite=strict; httponly';

let kiler: http.Server;
let kilerAddress: URL;
let vite: ViteDevServer;
let viteAddress: URL;
let seen: Seen[] = [];
let eventsClosed: Promise<void>;
let markEventsClosed: () => void;

/** A made-up Kiler: the event stream, the token exchange and a JSON answer for the rest. */
function fakeKiler(request: http.IncomingMessage, response: http.ServerResponse): void {
  seen.push({
    method: request.method ?? '',
    url: request.url ?? '',
    host: request.headers.host,
    fetchSite: request.headers['sec-fetch-site'] as string | undefined,
    cookie: request.headers.cookie,
  });

  if (request.url === '/api/events') {
    response.on('close', () => markEventsClosed());
    response.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache,no-store' });
    response.write('event: ping\ndata: \n\n');
    return;
  }

  if (request.url?.startsWith('/?t=')) {
    response.writeHead(302, { Location: '/', 'Set-Cookie': exchangeCookie, 'Cache-Control': 'no-store' });
    response.end();
    return;
  }

  response.writeHead(200, { 'Content-Type': 'application/json' });
  response.end('{"version":"0.1.0"}');
}

interface Answer {
  status: number;
  headers: http.IncomingHttpHeaders;
  body: string;
}

/** A request to the dev server, as the browser sends it from the page. */
function get(path: string, headers: http.OutgoingHttpHeaders = {}): Promise<Answer> {
  return new Promise((resolve, reject) => {
    const request = http.get(new URL(path, viteAddress), { headers, agent: false }, (response) => {
      let body = '';
      response.setEncoding('utf8');
      response.on('data', (chunk: string) => (body += chunk));
      response.on('end', () => resolve({ status: response.statusCode ?? 0, headers: response.headers, body }));
    });
    request.on('error', reject);
  });
}

function within<T>(promise: Promise<T>, ms: number, what: string): Promise<T> {
  return Promise.race([
    promise,
    new Promise<T>((_, reject) => setTimeout(() => reject(new Error(`${what} did not happen in ${ms} ms.`)), ms)),
  ]);
}

beforeAll(async () => {
  kiler = http.createServer(fakeKiler);
  await new Promise<void>((resolve) => kiler.listen(0, '127.0.0.1', resolve));
  kilerAddress = new URL(`http://127.0.0.1:${(kiler.address() as AddressInfo).port}`);
  vite = await startDevServer(kilerAddress, { port: 0, logLevel: 'silent' });
  viteAddress = devServerAddress(vite);
});

afterAll(async () => {
  await vite?.close();
  kiler?.closeAllConnections();
  await new Promise((resolve) => kiler?.close(resolve));
});

beforeEach(() => {
  seen = [];
  eventsClosed = new Promise((resolve) => (markEventsClosed = resolve));
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe('the dev server', () => {
  it('listens on 127.0.0.1 only', () => {
    const address = vite.httpServer?.address() as AddressInfo;

    expect(address.address).toBe('127.0.0.1');
    expect(viteAddress.hostname).toBe('127.0.0.1');
  });

  it('hands /api/ to Kiler with the Host of Kiler, and the cookie and Sec-Fetch-Site of the browser', async () => {
    const answer = await get('/api/health', {
      cookie: 'kiler-0123456789abcdef=made-up-token',
      'sec-fetch-site': 'same-origin',
    });

    expect(answer.status).toBe(200);
    expect(answer.body).toBe('{"version":"0.1.0"}');
    expect(seen).toEqual([
      {
        method: 'GET',
        url: '/api/health',
        host: kilerAddress.host,
        fetchSite: 'same-origin',
        cookie: 'kiler-0123456789abcdef=made-up-token',
      },
    ]);
  });

  it('hands the token exchange to Kiler and passes its cookie and redirect on as they are', async () => {
    const answer = await get('/?t=made-up-token', { 'sec-fetch-site': 'none' });

    expect(answer.status).toBe(302);
    expect(answer.headers.location).toBe('/');
    expect(answer.headers['set-cookie']).toEqual([exchangeCookie]);
    expect(seen.map((request) => [request.url, request.host, request.fetchSite])).toEqual([
      ['/?t=made-up-token', kilerAddress.host, 'none'],
    ]);
  });

  it.each(['/', '/index.html?t=made-up-token', '/apiary'])('serves %s itself', async (path) => {
    const answer = await get(path);

    expect(answer.status).toBe(200);
    expect(answer.body).toContain('<div id="root">');
    expect(seen).toEqual([]);
  });

  it('closes its event stream to Kiler when the browser closes its own', async () => {
    const opened = new Promise<http.ClientRequest>((resolve, reject) => {
      const request = http.get(new URL('/api/events', viteAddress), { agent: false }, (response) => {
        response.once('data', () => resolve(request));
      });
      request.on('error', reject);
    });
    const request = await within(opened, 5000, 'The first event');

    request.destroy();

    await within(eventsClosed, 5000, 'The close of the stream to Kiler');
  });

  // changeOrigin gives Kiler its own Host, so in dev mode Vite's host check is the guard against DNS rebinding.
  it.each(['/api/health', '/?t=made-up-token'])(
    'refuses %s with a foreign Host, and nothing reaches Kiler',
    async (path) => {
      const answer = await get(path, { host: `kiler.example:${viteAddress.port}` });

      expect(answer.status).toBe(403);
      expect(seen).toEqual([]);
    },
  );

  it('serves the page with headers that forbid framing', async () => {
    const answer = await get('/');

    expect(answer.headers['content-security-policy']).toBe("frame-ancestors 'none'");
    expect(answer.headers['x-frame-options']).toBe('DENY');
  });

  // Vite's client polls the WebSocket of a dev server that went away, with every cookie of 127.0.0.1.
  it.each(['vite-hmr', 'vite-ping'])('accepts no WebSocket (%s), so no page can poll for it', async (protocol) => {
    expect(await within(webSocket(protocol), 5000, 'The WebSocket handshake')).toBe('failed');
    expect(seen).toEqual([]);
  });
});

/** A WebSocket to the dev server, as Vite's client opens it: "open" or "failed". */
function webSocket(protocol: string): Promise<'open' | 'failed'> {
  return new Promise((resolve) => {
    const socket = new WebSocket(`ws://${viteAddress.host}/`, protocol);
    socket.addEventListener('open', () => {
      socket.close();
      resolve('open');
    });
    socket.addEventListener('error', () => resolve('failed'));
  });
}

describe('the dev server when Kiler has stopped', () => {
  it('logs the failed exchange without the token', async () => {
    const closed = http.createServer();
    await new Promise<void>((resolve) => closed.listen(0, '127.0.0.1', resolve));
    const port = (closed.address() as AddressInfo).port;
    await new Promise((resolve) => closed.close(resolve));
    const logged = vi.spyOn(console, 'error').mockImplementation(() => {});
    const server = await startDevServer(new URL(`http://127.0.0.1:${port}`), { port: 0, logLevel: 'error' });
    try {
      viteAddress = devServerAddress(server);

      const answer = await get('/?t=made-up-token');

      expect(answer.status).toBe(502);
      const output = logged.mock.calls.flat().join('\n');
      expect(output).toContain('http proxy error: /?t=…');
      expect(output).not.toContain('made-up-token');
    } finally {
      await server.close();
      viteAddress = devServerAddress(vite);
    }
  });
});

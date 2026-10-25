import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createLogger, createServer, type LogLevel, type Logger, type ProxyOptions, type ViteDevServer } from 'vite';
import { maskToken } from './kiler.ts';

/** The folder of the UI project, `src/ui`. */
export const uiFolder = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

/**
 * What the dev server hands to Kiler: the API, and the exchange of the token for the cookie (`GET /?t=<token>`).
 * Vite serves the rest, `/` without `t` included. The protection of Kiler stays on (kiler-ui skill):
 * - `changeOrigin` sets `Host` to the address of Kiler, which its guard requires (M1.4);
 * - the cookies, `Sec-Fetch-Site`, `Set-Cookie` and `Location: /` pass through as they are;
 * - when the browser closes `/api/events`, the proxy closes its request to Kiler (http-proxy-3 does it; the tests
 *   check it), so a closed page stops Kiler in dev mode too.
 */
export function kilerProxy(kiler: URL): Record<string, ProxyOptions> {
  const target = kiler.origin;
  return {
    // With the slash, so /apiary stays with Vite.
    '/api/': { target, changeOrigin: true },
    // A key that starts with ^ is a RegExp on the path with the query.
    '^/\\?t=': { target, changeOrigin: true },
  };
}

export interface DevServerOptions {
  /** The port of the dev server; Vite takes the next free one when it is busy. */
  port?: number;
  logLevel?: LogLevel;
}

/** Starts the Vite dev server of `src/ui` on 127.0.0.1 (vite.config.ts), with the proxy to Kiler. */
export async function startDevServer(kiler: URL, options: DevServerOptions = {}): Promise<ViteDevServer> {
  const server = await createServer({
    root: uiFolder,
    configFile: path.join(uiFolder, 'vite.config.ts'),
    customLogger: tokenMaskingLogger(options.logLevel),
    server: { port: options.port, proxy: kilerProxy(kiler) },
  });
  await server.listen();
  return server;
}

/**
 * Vite's logger, with the token hidden in every message. Vite logs the address of a proxy request that fails, such as
 * `http proxy error: /?t=<token>` when Kiler has stopped.
 */
export function tokenMaskingLogger(level?: LogLevel): Logger {
  const logger = createLogger(level);
  const { info, warn, warnOnce, error } = logger;
  logger.info = (message, options) => info(maskToken(message), options);
  logger.warn = (message, options) => warn(maskToken(message), options);
  logger.warnOnce = (message, options) => warnOnce(maskToken(message), options);
  logger.error = (message, options) => error(maskToken(message), options);
  return logger;
}

/** The address of the dev server, `http://127.0.0.1:<port>/`. */
export function devServerAddress(server: ViteDevServer): URL {
  const address = server.resolvedUrls?.local[0];
  if (address === undefined) {
    throw new Error('The dev server does not listen.');
  }

  return new URL(address);
}

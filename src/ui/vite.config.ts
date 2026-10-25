/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react';
import { defineConfig, type Plugin } from 'vite';

/**
 * Refuses every WebSocket handshake at once. Without the WebSocket server, Vite's proxy leaves a handshake it does not
 * take open until the dev server stops, and then Vite's client tries once more, on a port that is free by then.
 * Refused at once, both of its tries happen while the page loads.
 */
const refuseWebSockets: Plugin = {
  name: 'kiler-refuse-websockets',
  configureServer(server) {
    server.httpServer?.on('upgrade', (_request, socket) => {
      socket.end('HTTP/1.1 404 Not Found\r\nConnection: close\r\n\r\n');
    });
  },
};

// The proxy to Kiler is not here: `npm run dev` starts Kiler first and gives its address to the dev server
// (dev/vite-server.ts).
export default defineConfig({
  plugins: [react(), refuseWebSockets],
  server: {
    // Loopback only, as Kiler itself (M1.3). The page is opened on 127.0.0.1, not on localhost (M1.5).
    host: '127.0.0.1',
    // No WebSocket, so no HMR (decided in M1.7): when the dev server goes away, Vite's client would try its port every
    // second, with every cookie of 127.0.0.1, which is the reconnect the event stream avoids. Reload by hand (F5).
    ws: false,
    // Kiler's guard keeps other sites from framing its pages (Sec-Fetch-Site); in dev mode Vite serves the page.
    headers: {
      'Content-Security-Policy': "frame-ancestors 'none'",
      'X-Frame-Options': 'DENY',
    },
  },
  preview: {
    host: '127.0.0.1',
  },
  build: {
    // Browsers with light-dark() (index.css). For older ones, lightningcss would rewrite it into toggle variables that
    // resolve once on the root, so a part of the page with a color-scheme of its own (M6, M7) would not follow it.
    cssTarget: ['chrome123', 'edge123', 'firefox120'],
  },
  worker: {
    // The events worker is a module worker (src/events.ts), so its build is a module too.
    format: 'es',
  },
  test: {
    environment: 'node',
  },
});

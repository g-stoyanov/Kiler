// `npm run dev`: Kiler from `dotnet run`, then the Vite dev server with the proxy to it, then the browser on the
// dev server with the token, which the proxy exchanges for the cookie. When Kiler stops (its last page closed, or
// Ctrl+C), the dev server stops too.
//
// `npm run dev -- --no-browser` opens no browser and prints the address with the token instead, as Kiler does.
import { spawn } from 'node:child_process';
import path from 'node:path';
import { startKiler } from './kiler.ts';
import { devServerAddress, startDevServer, uiFolder } from './vite-server.ts';

const args = process.argv.slice(2);
const unknown = args.filter((arg) => arg !== '--no-browser');
if (unknown.length > 0) {
  console.error(`Unknown parameter: ${unknown.join(' ')}. The only one is --no-browser.`);
  process.exit(2);
}

const noBrowser = args.includes('--no-browser');
const repoFolder = path.resolve(uiFolder, '..', '..');
// Out of git (.gitignore). A data folder of its own makes this Kiler an instance of its own.
const dataFolder = path.join(repoFolder, 'dev-data');

console.log('Starting Kiler with dotnet run…');
const kiler = await startKiler(repoFolder, dataFolder).catch((error: unknown) => {
  console.error(error instanceof Error ? error.message : error);
  process.exit(1);
});

const { vite, address } = await startDevServer(kiler.server)
  .then((vite) => ({ vite, address: devServerAddress(vite) }))
  .catch((error: unknown) => {
    console.error(error instanceof Error ? error.message : error);
    kiler.kill();
    process.exit(1);
  });
vite.printUrls();

void kiler.exited.then(async (code) => {
  console.log('Kiler has stopped, so the dev server stops too.');
  await vite.close();
  process.exit(code);
});

// Ctrl+C reaches Kiler as well, and its stop ends the dev server above. This only waits for it, and ends both when
// Kiler does not stop.
process.on('SIGINT', () => {
  setTimeout(() => {
    console.error('Kiler did not stop in 10 s after Ctrl+C; ending it.');
    kiler.kill();
    void vite.close().finally(() => process.exit(1));
  }, 10_000).unref();
});

const page = new URL(`/?t=${kiler.token}`, address);
if (noBrowser) {
  console.log(`Open: ${page.href}`);
} else {
  openBrowser(page);
}

/**
 * The default browser through the shell, as Kiler opens it. The address is on 127.0.0.1, so nothing in it reaches a
 * command interpreter. It is printed only when the browser does not open, because it holds the token.
 */
function openBrowser(address: URL): void {
  const shell = spawn('rundll32.exe', ['url.dll,FileProtocolHandler', address.href], { stdio: 'ignore' });
  shell.on('error', () => {
    console.log(`The browser did not open. Open: ${address.href}`);
  });
  shell.unref();
}

import { spawn } from 'node:child_process';
import path from 'node:path';
import { createInterface } from 'node:readline';

/** A Kiler started for the dev mode. */
export interface DevKiler {
  /** The address of Kiler, `http://127.0.0.1:<port>`. */
  server: URL;
  /** The token from the `Open:` line. It is never printed. */
  token: string;
  /** Completes with the exit code when Kiler has stopped. */
  exited: Promise<number>;
  /** Ends Kiler at once, when it does not stop by itself. */
  kill(): void;
}

/** The address of Kiler and its token, from its line `Open: http://127.0.0.1:<port>/?t=<token>`. */
export interface OpenLine {
  server: URL;
  token: string;
}

const openPrefix = 'Open: ';
const alreadyRunning = 'Kiler is already running';
/** `AccessToken`: 32 bytes as base64url without padding. */
const tokenPattern = /^[A-Za-z0-9_-]{43}$/;
/** The first `dotnet run` builds Kiler first. */
const startTimeoutMs = 180_000;

/** Reads the `Open:` line of Kiler. Any other line, or an address that is not the one Kiler prints, gives null. */
export function parseOpenLine(line: string): OpenLine | null {
  if (!line.startsWith(openPrefix)) {
    return null;
  }

  let address: URL;
  try {
    address = new URL(line.slice(openPrefix.length).trim());
  } catch {
    return null;
  }

  const token = address.searchParams.get('t');
  const onlyToken = [...address.searchParams.keys()].length === 1;
  if (
    address.protocol !== 'http:' ||
    address.hostname !== '127.0.0.1' ||
    address.port === '' ||
    address.pathname !== '/' ||
    address.username !== '' ||
    address.hash !== '' ||
    !onlyToken ||
    token === null ||
    !tokenPattern.test(token)
  ) {
    return null;
  }

  return { server: new URL(address.origin), token };
}

/** The text as it may be printed: the value of every `t` parameter in an address, the token, is hidden. */
export function maskToken(text: string): string {
  return text.replace(/([?&]t=)[^&#\s]*/g, '$1…');
}

/**
 * Starts Kiler with `dotnet run`, without a browser and with its own data folder, so it is an instance of its own and
 * does not meet the Kiler of the user (M1.5). Its output goes on to the console with the token hidden. Resolves when
 * Kiler prints its address.
 */
export function startKiler(repoFolder: string, dataFolder: string): Promise<DevKiler> {
  const project = path.join(repoFolder, 'src', 'Kiler.App');
  const child = spawn('dotnet', ['run', '--project', project, '--', '--no-browser', '--data', dataFolder], {
    cwd: repoFolder,
    // The console is shared, so Ctrl+C reaches Kiler too and stops it as usual.
    stdio: ['ignore', 'pipe', 'inherit'],
  });
  const exited = new Promise<number>((resolve) => child.on('close', (code) => resolve(code ?? 1)));
  // `dotnet run` starts Kiler as a process of its own, which ending dotnet would leave running.
  const kill = () => {
    if (child.pid !== undefined && child.exitCode === null) {
      spawn('taskkill.exe', ['/pid', `${child.pid}`, '/t', '/f'], { stdio: 'ignore' });
    }
  };

  return new Promise<DevKiler>((resolve, reject) => {
    let found: OpenLine | null = null;
    let second = false;
    const timer = setTimeout(() => {
      kill();
      reject(new Error(`Kiler did not print its address in ${startTimeoutMs / 1000} s.`));
    }, startTimeoutMs);

    const lines = createInterface({ input: child.stdout });
    lines.on('line', (line) => {
      console.log(maskToken(line));
      if (line.startsWith(alreadyRunning)) {
        second = true;
      }

      const open = found === null ? parseOpenLine(line) : null;
      if (open === null) {
        return;
      }

      found = open;
      clearTimeout(timer);
      if (second) {
        reject(new Error('The dev Kiler is already running. Stop the other `npm run dev` first.'));
      } else {
        resolve({ ...open, exited, kill });
      }
    });

    child.on('error', (error) => {
      clearTimeout(timer);
      reject(new Error(`dotnet does not start: ${error.message}`));
    });
    void exited.then((code) => {
      clearTimeout(timer);
      if (found === null) {
        reject(new Error(`Kiler stopped before it printed its address (exit code ${code}).`));
      }
    });
  });
}

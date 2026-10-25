import { useEffect, useState } from 'react';
import { getHealth, messageOf, type Health } from './api.ts';
import { connectEvents } from './connection.ts';
import type { ConnectionState } from './events.ts';
import { texts } from './texts.ts';
import { applyThemeChoice, readThemeChoice, saveThemeChoice, themeChoices, type ThemeChoice } from './theme.ts';

/** The frame of every page: the header with the connection and the theme, and the content below it. */
export function App() {
  const [themeChoice, setThemeChoice] = useState(() => readThemeChoice());
  const connection = useConnection();
  const health = useHealth();

  const changeTheme = (choice: ThemeChoice) => {
    applyThemeChoice(choice);
    saveThemeChoice(choice);
    setThemeChoice(choice);
  };

  return (
    <div className="frame">
      <header className="frame-header">
        <span className="brand">{texts.appName}</span>
        <ConnectionBadge state={connection} />
        <label className="theme-switch">
          {texts.theme.label}
          <select value={themeChoice} onChange={(event) => changeTheme(event.target.value as ThemeChoice)}>
            {themeChoices.map((choice) => (
              <option key={choice} value={choice}>
                {texts.theme[choice]}
              </option>
            ))}
          </select>
        </label>
      </header>
      {connection === 'stopped' && (
        <div className="banner banner-danger" role="alert">
          {texts.connection.stoppedMessage}
        </div>
      )}
      <main className="frame-main">
        <section className="card">
          <h1>{texts.home.title}</h1>
          <HealthLine health={health} />
        </section>
      </main>
    </div>
  );
}

function ConnectionBadge({ state }: { state: ConnectionState }) {
  return (
    <span className={`badge badge-${state}`} role="status">
      {texts.connection[state]}
    </span>
  );
}

type HealthResult = { state: 'loading' } | { state: 'ok'; health: Health } | { state: 'error'; message: string };

function HealthLine({ health }: { health: HealthResult }) {
  switch (health.state) {
    case 'loading':
      return <p className="muted">{texts.home.loading}</p>;
    case 'ok':
      return <p>{texts.home.version(health.health.version)}</p>;
    case 'error':
      return (
        <p className="banner banner-danger" role="alert">
          {health.message}
        </p>
      );
  }
}

function useConnection(): ConnectionState {
  const [state, setState] = useState<ConnectionState>('connecting');
  useEffect(() => connectEvents(setState), []);
  return state;
}

function useHealth(): HealthResult {
  const [result, setResult] = useState<HealthResult>({ state: 'loading' });
  useEffect(() => {
    const abort = new AbortController();
    getHealth(abort.signal).then(
      (health) => setResult({ state: 'ok', health }),
      (error: unknown) => {
        if (!abort.signal.aborted) {
          setResult({ state: 'error', message: messageOf(error) });
        }
      },
    );
    return () => abort.abort();
  }, []);
  return result;
}

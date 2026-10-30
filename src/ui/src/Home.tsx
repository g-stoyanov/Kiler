import { useEffect, useState } from 'react';
import { getHealth, messageOf, type Health, type Session } from './api.ts';
import { texts } from './texts.ts';

/**
 * The start page of a signed-in user or a guest (the routes send a page with neither to the sign-in); the records come
 * in M5 and M6. After a lock in guest mode, it shows the guest and says so.
 */
export function Home({ session, locked }: { session: Session; locked: boolean }) {
  const health = useHealth();
  return (
    <section className="card stack">
      <h1>{texts.home.title}</h1>
      {locked && (
        <p className="banner banner-warning" role="status">
          {texts.home.locked}
        </p>
      )}
      <p>{session.signedIn ? texts.home.signedIn(session.displayName ?? '') : texts.home.guest}</p>
      {session.signedIn && <p>{texts.home.levels(session.levels.join(', '))}</p>}
      {session.canEdit && <p>{texts.home.editor}</p>}
      {session.isAdmin && <p>{texts.home.admin}</p>}
      <HealthLine health={health} />
    </section>
  );
}

type HealthResult = { state: 'loading' } | { state: 'ok'; health: Health } | { state: 'error'; message: string };

function HealthLine({ health }: { health: HealthResult }) {
  switch (health.state) {
    case 'loading':
      return <p className="muted">{texts.home.loading}</p>;
    case 'ok':
      return <p className="muted">{texts.home.version(health.health.version)}</p>;
    case 'error':
      return (
        <p className="banner banner-danger" role="alert">
          {health.message}
        </p>
      );
  }
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

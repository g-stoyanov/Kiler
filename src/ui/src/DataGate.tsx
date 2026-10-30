import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useNavigate } from 'react-router';
import { getData, messageOf, missingFile, type DataInfo } from './api.ts';
import { isEmptyCopy, nextRead, showsRoutes } from './data.ts';
import { useSessionStore } from './session.ts';
import { texts } from './texts.ts';
import { Wizard } from './Wizard.tsx';

type DataResult = { state: 'reading' } | { state: 'read'; data: DataInfo } | { state: 'failed'; message: string };

/**
 * The state of the data (M4.2) in front of the routes: its reason while Kiler has no data, the wizard where a new Kiler
 * may be made, and the routes once the local copy is open or offline. `GET /api/data` is no activity for the automatic
 * lock, so it is asked again on a timer: often while the copy opens, now and then once it is open, to see it go
 * offline and back. Never while the wizard shows, so a new state does not take its recovery key away.
 *
 * The session that the store read at the connect may be of a copy that was still opening, without its guest mode, so
 * once the routes show after a state that did not show them, the session is read again; that covers the wizard too.
 */
export function DataGate({ children }: { children: ReactNode }) {
  const store = useSessionStore();
  const navigate = useNavigate();
  const [result, setResult] = useState<DataResult>({ state: 'reading' });
  // A new value reads the state again, at once; the last state shows meanwhile, so the wizard keeps its form.
  const [round, setRound] = useState(0);
  const again = () => setRound((value) => value + 1);
  // Whether the last state showed the routes; at first as the read of the session at the connect assumes.
  const showedRoutes = useRef(true);

  useEffect(() => {
    const abort = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    const read = () => {
      getData(abort.signal).then(
        (data) => {
          if (abort.signal.aborted) {
            return;
          }

          setResult({ state: 'read', data });
          const routes = showsRoutes(data);
          if (routes && !showedRoutes.current) {
            store.reload();
          }

          showedRoutes.current = routes;
          const wait = nextRead(data);
          if (wait !== undefined) {
            timer = setTimeout(read, wait);
          }
        },
        (error: unknown) => {
          if (!abort.signal.aborted) {
            setResult({ state: 'failed', message: messageOf(error) });
          }
        },
      );
    };
    read();
    return () => {
      abort.abort();
      clearTimeout(timer);
    };
  }, [round, store]);

  if (result.state === 'reading') {
    return <p className="muted">{texts.data.reading}</p>;
  }

  if (result.state === 'failed') {
    return (
      <section className="card stack">
        <p className="banner banner-danger" role="alert">
          {result.message}
        </p>
        <div>
          <button type="button" onClick={again}>
            {texts.session.tryAgain}
          </button>
        </div>
      </section>
    );
  }

  const data = result.data;
  if (data.state === 'missing' || isEmptyCopy(data)) {
    // After the wizard the admin signs in; the session is read again once the new copy shows the routes.
    const done = () => {
      again();
      void navigate('/sign-in');
    };
    return <Wizard onRefused={again} onDone={done} />;
  }

  if (data.state !== 'open' && data.state !== 'offline') {
    return <DataProblem folder={data.folder} lines={[texts.data[data.state]]} />;
  }

  if (data.levels !== null) {
    const firstStart = data.state === 'offline' && data.levels === missingFile && data.settings === missingFile;
    return (
      <DataProblem
        folder={data.folder}
        lines={
          firstStart
            ? [texts.data.offlineNew]
            : [
                texts.data.levels(data.levels),
                ...(data.settings === null ? [] : [texts.data.settings(data.settings)]),
                texts.data.levelsDamaged,
              ]
        }
      />
    );
  }

  return (
    <>
      {data.state === 'offline' && (
        <p className="banner banner-warning" role="status">
          {texts.data.offline}
        </p>
      )}
      {data.settings !== null && (
        <p className="banner banner-warning" role="status">
          {texts.data.settings(data.settings)}{' '}
          {data.settings === missingFile ? texts.data.halfMade : texts.data.settingsDamaged}
        </p>
      )}
      {children}
    </>
  );
}

function DataProblem({ folder, lines }: { folder: string | null; lines: string[] }) {
  return (
    <section className="card stack">
      {lines.map((line) => (
        <p key={line}>{line}</p>
      ))}
      {folder !== null && <p className="muted">{texts.data.folder(folder)}</p>}
    </section>
  );
}

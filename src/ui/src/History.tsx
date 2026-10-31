import { useEffect, useState } from 'react';
import { Link, useNavigate } from 'react-router';
import {
  ApiError,
  getHistory,
  getRecord,
  messageOf,
  restoreVersion,
  type RecordData,
  type RecordVersion,
  type Session,
} from './api.ts';
import { useChanges } from './changes.ts';
import { localDateTime, recordPath } from './fields.ts';
import { offersVersionRestore } from './history.ts';
import './History.css';
import { authorName, readsSession } from './recordView.ts';
import { useSessionStore } from './session.ts';
import { texts } from './texts.ts';

type ReadState =
  | { state: 'reading' }
  | { state: 'ready'; record: RecordData; versions: RecordVersion[] }
  | { state: 'notThere' }
  | { state: 'failed'; message: string };

/**
 * The versions of a record at `/records/<id>/history` (M6.14, design 5.6): who changed it and when, the newest first,
 * the current one with them, and "Restore" of an old one for an editor who holds the levels of the current version
 * and of the old one. A restore is over the current version that the page read; a conflict reads the history again.
 * `App.tsx` mounts it again for another `id`.
 */
export function History({ id, session }: { id: string; session: Session }) {
  const store = useSessionStore();
  const navigate = useNavigate();
  const changes = useChanges();
  const [read, setRead] = useState<ReadState>({ state: 'reading' });
  const [round, setRound] = useState(0);
  const [restoring, setRestoring] = useState<number | null>(null);
  const [refusal, setRefusal] = useState<string | null>(null);

  useEffect(() => {
    const abort = new AbortController();
    Promise.all([getRecord(id, abort.signal), getHistory(id, abort.signal)]).then(
      ([record, versions]) => {
        if (!abort.signal.aborted) {
          setRead({ state: 'ready', record, versions });
        }
      },
      (error: unknown) => {
        if (abort.signal.aborted) {
          return;
        }

        if (error instanceof ApiError && error.kind === 'refused' && error.status === 404) {
          setRead({ state: 'notThere' });
          return;
        }

        setRead({ state: 'failed', message: messageOf(error) });
        if (readsSession(error)) {
          store.reload();
        }
      },
    );
    return () => abort.abort();
  }, [id, changes, round, store]);

  if (read.state === 'reading') {
    return <p className="muted">{texts.history.reading}</p>;
  }

  if (read.state === 'notThere') {
    return (
      <section className="card stack">
        <p>{texts.record.notThere}</p>
        <div>
          <Link className="button" to="/">
            {texts.record.toMain}
          </Link>
        </div>
      </section>
    );
  }

  if (read.state === 'failed') {
    return (
      <section className="card stack">
        <p className="banner banner-danger" role="alert">
          {read.message}
        </p>
        <div>
          <button
            type="button"
            onClick={() => {
              setRead({ state: 'reading' });
              setRound((value) => value + 1);
            }}
          >
            {texts.history.tryAgain}
          </button>
        </div>
      </section>
    );
  }

  const { record, versions } = read;
  const current = versions[0];
  const restore = (old: RecordVersion) => {
    if (restoring !== null || current === undefined) {
      return;
    }

    setRestoring(old.version);
    setRefusal(null);
    restoreVersion(id, old.version, current.version).then(
      () => navigate(recordPath(id)),
      (error: unknown) => {
        setRestoring(null);
        setRefusal(messageOf(error));
        if (readsSession(error)) {
          store.reload();
        } else if (error instanceof ApiError && error.kind === 'refused') {
          setRound((value) => value + 1);
        }
      },
    );
  };

  return (
    <section className="card card-wide stack">
      <h1>{texts.history.title}</h1>
      <p className="history-record">
        <Link to={recordPath(id)}>{record.name}</Link>
      </p>
      {refusal !== null && (
        <p className="banner banner-danger" role="alert">
          {refusal}
        </p>
      )}
      <div className="table-frame">
        <table className="table history-table">
          <thead>
            <tr>
              <th>{texts.history.version}</th>
              <th>{texts.history.changedBy}</th>
              <th>{texts.history.changedAt}</th>
              <th>
                <span className="visually-hidden">{texts.history.actions}</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {versions.map((version) => (
              <tr key={version.version} className={version === current ? 'chosen' : undefined}>
                <td>{version === current ? texts.history.current(version.version) : String(version.version)}</td>
                <td>{authorName(version.updatedBy)}</td>
                <td>{localDateTime(version.updatedAt)}</td>
                <td>
                  {offersVersionRestore(session, versions, version) && (
                    <button type="button" onClick={() => restore(version)} disabled={restoring !== null}>
                      {restoring === version.version ? texts.history.restoring : texts.history.restore}
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {versions.length === 1 && <p className="muted">{texts.history.onlyCurrent}</p>}
    </section>
  );
}

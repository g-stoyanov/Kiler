import { useEffect, useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { ApiError, getTemplates, getTrash, messageOf, restoreFromTrash, type Session, type TrashItem } from './api.ts';
import { useChanges } from './changes.ts';
import { localDateTime, recordPath } from './fields.ts';
import { offersTrashRestore, pathText } from './history.ts';
import './History.css';
import { authorName, readsSession } from './recordView.ts';
import { useSessionStore } from './session.ts';
import { texts } from './texts.ts';

type ListState =
  | { state: 'reading' }
  | { state: 'ready'; items: TrashItem[]; templates: Map<string, string> }
  | { state: 'failed'; message: string };

/**
 * The trash at `/trash` (M6.14, design 5.5): the deleted records, each with "Restore" for an editor who holds every
 * level of it, which brings it back as it was deleted and opens it. A guest reads the trash too (`GET /api/trash` is
 * `Reader`). It reads the trash again at each change of other users, and after a restore that another Kiler has made
 * meanwhile (404) or that cannot be made now (409).
 */
export function Trash({ session }: { session: Session }) {
  const store = useSessionStore();
  const navigate = useNavigate();
  const changes = useChanges();
  const [list, setList] = useState<ListState>({ state: 'reading' });
  // A new value reads the trash again; the last list shows meanwhile.
  const [round, setRound] = useState(0);
  const [restoring, setRestoring] = useState<string | null>(null);
  const [refusal, setRefusal] = useState<string | null>(null);

  useEffect(() => {
    const abort = new AbortController();
    Promise.all([getTrash(abort.signal), getTemplates(abort.signal)]).then(
      ([items, templates]) => {
        if (!abort.signal.aborted) {
          const names = new Map(templates.templates.map((template) => [template.id, template.name]));
          setList({ state: 'ready', items, templates: names });
        }
      },
      (error: unknown) => {
        if (!abort.signal.aborted) {
          setList({ state: 'failed', message: messageOf(error) });
          if (readsSession(error)) {
            store.reload();
          }
        }
      },
    );
    return () => abort.abort();
  }, [changes, round, store]);

  const restore = (id: string) => {
    if (restoring !== null) {
      return;
    }

    setRestoring(id);
    setRefusal(null);
    restoreFromTrash(id).then(
      (record) => navigate(recordPath(record.id)),
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

  if (list.state === 'reading') {
    return <p className="muted">{texts.history.readingTrash}</p>;
  }

  if (list.state === 'failed') {
    return (
      <section className="card stack">
        <h1>{texts.history.trashTitle}</h1>
        <p className="banner banner-danger" role="alert">
          {list.message}
        </p>
        <div>
          <button
            type="button"
            onClick={() => {
              setList({ state: 'reading' });
              setRound((value) => value + 1);
            }}
          >
            {texts.history.tryAgain}
          </button>
        </div>
      </section>
    );
  }

  return (
    <section className="card card-wide stack">
      <h1>{texts.history.trashTitle}</h1>
      {refusal !== null && (
        <p className="banner banner-danger" role="alert">
          {refusal}
        </p>
      )}
      {list.items.length === 0 ? (
        <p className="muted">{texts.history.trashEmpty}</p>
      ) : (
        <div className="table-frame">
          <table className="table history-table">
            <thead>
              <tr>
                <th>{texts.history.name}</th>
                <th>{texts.history.template}</th>
                <th>{texts.history.path}</th>
                <th>{texts.history.lastChanged}</th>
                <th>
                  <span className="visually-hidden">{texts.history.actions}</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {list.items.map((item) => (
                <tr key={item.id}>
                  <td>{item.name}</td>
                  <td>{list.templates.get(item.template) ?? item.template}</td>
                  <td>{pathText(item.stash)}</td>
                  <td>{texts.history.by(authorName(item.updatedBy), localDateTime(item.updatedAt))}</td>
                  <td>
                    {offersTrashRestore(session, item) && (
                      <button type="button" onClick={() => restore(item.id)} disabled={restoring !== null}>
                        {restoring === item.id ? texts.history.restoring : texts.history.restore}
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <div>
        <Link to="/">{texts.history.toMain}</Link>
      </div>
    </section>
  );
}

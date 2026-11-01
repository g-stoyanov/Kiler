import { useEffect, useId, useState } from 'react';
import { Link } from 'react-router';
import { getAudit, messageOf, type AuditAnswer } from './api.ts';
import { afterAdminRefusal } from './admin.ts';
import { AdminRefusal } from './AdminRefusal.tsx';
import { auditRows, dayBounds, dayOf, shiftDay } from './audit.ts';
import './Audit.css';
import { texts } from './texts.ts';

type AuditRead =
  | { state: 'reading' }
  | { state: 'ready'; answer: AuditAnswer }
  | { state: 'failed'; message: string; signInAgain: boolean };

/**
 * The audit at `/audit` (M6.19): the views of the secrets of the top level on one local day, today at first, read from
 * the shared folder at each choice of the day. One day at a time, as the answer has no cap of its size. The day stays
 * above the table while it reads, so "Previous" and "Next" keep the focus.
 */
export function Audit() {
  const id = useId();
  // The date field keeps what is typed; the day that is read changes only when that is a whole day.
  const [typed, setTyped] = useState(() => dayOf(new Date()));
  const [day, setDay] = useState(typed);
  // A new value reads the day again.
  const [round, setRound] = useState(0);
  // The last read, with the day and the round it is of: one of another is still reading.
  const [read, setRead] = useState<{ of: string; read: AuditRead }>();
  const of = `${day}:${String(round)}`;

  useEffect(() => {
    const bounds = dayBounds(day);
    if (bounds === undefined) {
      return;
    }

    const abort = new AbortController();
    getAudit(bounds.from, bounds.to, abort.signal).then(
      (answer) => {
        if (!abort.signal.aborted) {
          setRead({ of, read: { state: 'ready', answer } });
        }
      },
      // As on the other admin screens, a refused read only says why.
      (error: unknown) => {
        if (!abort.signal.aborted) {
          setRead({
            of,
            read: {
              state: 'failed',
              message: messageOf(error),
              signInAgain: afterAdminRefusal(error) === 'signInAgain',
            },
          });
        }
      },
    );
    return () => abort.abort();
  }, [day, of]);

  const choose = (chosen: string) => {
    setTyped(chosen);
    if (dayBounds(chosen) !== undefined) {
      setDay(chosen);
    }
  };

  return (
    <section className="card card-wide stack">
      <h1>{texts.audit.title}</h1>
      <p className="muted">{texts.audit.intro}</p>
      <div className="audit-day">
        <label htmlFor={`${id}-day`}>{texts.audit.day}</label>
        <input id={`${id}-day`} type="date" value={typed} onChange={(event) => choose(event.target.value)} />
        <button type="button" onClick={() => choose(shiftDay(day, -1))}>
          {texts.audit.previous}
        </button>
        <button type="button" onClick={() => choose(shiftDay(day, 1))}>
          {texts.audit.next}
        </button>
      </div>
      <AuditDay
        read={read?.of === of ? read.read : { state: 'reading' }}
        onTryAgain={() => setRound((value) => value + 1)}
      />
    </section>
  );
}

function AuditDay({ read, onTryAgain }: { read: AuditRead; onTryAgain: () => void }) {
  if (read.state === 'reading') {
    return <p className="muted">{texts.audit.reading}</p>;
  }

  if (read.state === 'failed') {
    return (
      <>
        <AdminRefusal message={read.message} signInAgain={read.signInAgain} />
        <div>
          <button type="button" onClick={onTryAgain}>
            {texts.audit.tryAgain}
          </button>
        </div>
      </>
    );
  }

  const { entries, skipped } = read.answer;
  return (
    <>
      {skipped > 0 && (
        <p className="banner banner-warning" role="status">
          {texts.audit.skipped(skipped)}
        </p>
      )}
      {entries.length === 0 ? (
        <p className="muted">{texts.audit.none}</p>
      ) : (
        <div className="table-frame">
          <table className="table audit-table">
            <thead>
              <tr>
                <th>{texts.audit.time}</th>
                <th>{texts.audit.account}</th>
                <th>{texts.audit.record}</th>
                <th>{texts.audit.field}</th>
                <th>{texts.audit.use}</th>
              </tr>
            </thead>
            <tbody>
              {auditRows(entries).map((row) => (
                <tr key={row.key}>
                  <td>{row.time}</td>
                  <td>{row.account}</td>
                  <td>
                    <Link to={row.link}>{row.name}</Link>
                  </td>
                  <td>{row.field}</td>
                  <td>{row.use}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}

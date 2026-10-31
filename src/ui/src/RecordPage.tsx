import { useEffect, useState, type ReactNode } from 'react';
import { Link, useLocation } from 'react-router';
import {
  ApiError,
  getRecord,
  getTemplates,
  messageOf,
  requestSecret,
  type FieldType,
  type RecordData,
  type RecordField,
  type Session,
  type Template,
} from './api.ts';
import { useChanges } from './changes.ts';
import { CodeBlock } from './CodeBlock.tsx';
import { CopyButton } from './CopyButton.tsx';
import { FieldValue } from './FieldValue.tsx';
import { localDateTime, recordPath } from './fields.ts';
import {
  authorName,
  fieldRows,
  hideAtSessionChange,
  offersEdit,
  offersSecret,
  padlockAction,
  pathParts,
  readsSession,
  ShownSecrets,
} from './recordView.ts';
import './RecordPage.css';
import { SecretField } from './SecretField.tsx';
import { useSessionStore } from './session.ts';
import { signInFrom } from './signIn.ts';
import { texts } from './texts.ts';

type ReadState =
  | { state: 'reading' }
  | { state: 'ready'; record: RecordData; template: Template | undefined }
  | { state: 'notThere' }
  | { state: 'failed'; message: string };

/**
 * A record at `/records/<id>` (M6.5, design 8.2): its header, its fields in the order of the template with "Copy" on
 * each value, the padlocks of the levels that the session does not hold, and "Show" and "Copy" of a `secret`.
 * `App.tsx` mounts it again for another `id` and when the session signs in or out or its levels change.
 *
 * It reads the record again at each change of other users (`useChanges()`), and keeps the last one on the screen
 * meanwhile. A shown `secret` hides again after its time, on leaving the page, at a `session` event and at each read.
 */
export function RecordPage({ id, session }: { id: string; session: Session }) {
  const store = useSessionStore();
  const changes = useChanges();
  const [read, setRead] = useState<ReadState>({ state: 'reading' });
  // A new value reads the record again, for "Try again".
  const [round, setRound] = useState(0);
  const [secrets] = useState(
    () =>
      new ShownSecrets(
        (key, signal) => requestSecret(id, key, 'show', signal),
        (error) => {
          if (readsSession(error)) {
            store.reload();
          }
        },
      ),
  );

  useEffect(() => hideAtSessionChange(store, secrets), [store, secrets]);
  useEffect(() => () => secrets.hideAll(), [secrets]);

  useEffect(() => {
    secrets.hideAll();
    const abort = new AbortController();
    Promise.all([getRecord(id, abort.signal), getTemplates(abort.signal)]).then(
      ([record, templates]) => {
        if (!abort.signal.aborted) {
          const template = templates.templates.find((entry) => entry.id === record.template);
          setRead({ state: 'ready', record, template });
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
  }, [id, changes, round, secrets, store]);

  switch (read.state) {
    case 'reading':
      return <p className="muted">{texts.record.reading}</p>;
    case 'notThere':
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
    case 'failed':
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
              {texts.record.tryAgain}
            </button>
          </div>
        </section>
      );
    case 'ready':
      return <RecordView record={read.record} template={read.template} session={session} secrets={secrets} />;
  }
}

function RecordView({
  record,
  template,
  session,
  secrets,
}: {
  record: RecordData;
  template: Template | undefined;
  session: Session;
  secrets: ShownSecrets;
}) {
  const path = pathParts(record.stash);
  return (
    <article className="card card-wide stack record">
      {path.length > 0 && (
        <nav className="record-path" aria-label={texts.record.path}>
          {path.map((part) => (
            <Link key={part.id} to={part.to} className={part.name === null ? 'muted' : undefined}>
              {part.name ?? texts.record.unknownStash}
            </Link>
          ))}
        </nav>
      )}
      <header className="record-header">
        <div className="record-title">
          <h1>{record.name}</h1>
          <span className="mark">{template?.name ?? record.template}</span>
        </div>
        {offersEdit(session, record) && (
          <Link className="button" to={`${recordPath(record.id)}/edit`}>
            {texts.record.edit}
          </Link>
        )}
      </header>
      {record.fields.length === 0 && (
        <p className="banner banner-warning" role="status">
          {texts.record.noTemplate(record.template)}
        </p>
      )}
      <dl className="record-fields">
        <FieldRowView label={texts.record.description}>
          <Padlocked field={record.description} session={session} lines />
        </FieldRowView>
        {record.tags.length > 0 && (
          <FieldRowView label={texts.record.tags}>
            <span className="marks">
              {record.tags.map((tag) => (
                <span key={tag} className="mark">
                  {tag}
                </span>
              ))}
            </span>
          </FieldRowView>
        )}
        {fieldRows(record, template).map((row) => (
          <FieldRowView key={row.field.key} label={row.label}>
            {row.field.kind === 'secret' ? (
              <SecretField
                recordId={record.id}
                field={row.field}
                label={row.label}
                buttons={offersSecret(session)}
                secrets={secrets}
              />
            ) : row.code !== null ? (
              <CodeBlock code={row.code.code} language={row.code.language} dialect={row.code.dialect} />
            ) : (
              <span className="record-value">
                <Padlocked field={row.field} session={session} type={row.type} />
                {row.copy !== null && <CopyButton value={row.copy} label={texts.record.copyLabel(row.label)} />}
              </span>
            )}
          </FieldRowView>
        ))}
      </dl>
      <footer className="record-authors muted">
        <span>{texts.record.created(authorName(record.createdBy), localDateTime(record.createdAt))}</span>
        <span>{texts.record.changed(authorName(record.updatedBy), localDateTime(record.updatedAt))}</span>
      </footer>
    </article>
  );
}

function FieldRowView({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="record-field">
      <dt>{label}</dt>
      <dd>{children}</dd>
    </div>
  );
}

/**
 * A field by `FieldValue`, and beside its padlock "Sign in" for a guest, which comes back to the record (decided at the
 * start of M6).
 */
function Padlocked({
  field,
  session,
  type,
  lines = false,
}: {
  field: RecordField;
  session: Session;
  type?: FieldType;
  lines?: boolean;
}) {
  const location = useLocation();
  const value = <FieldValue field={field} type={lines ? 'multiline' : type} />;
  if (field.kind !== 'locked' || padlockAction(session) !== 'signIn') {
    return value;
  }

  return (
    <span className="record-padlock">
      {value}
      <Link className="button" to="/sign-in" state={signInFrom(location)}>
        {texts.account.signIn}
      </Link>
    </span>
  );
}

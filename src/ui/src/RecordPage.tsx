import { useEffect, useState, type ReactNode } from 'react';
import { Link, useLocation } from 'react-router';
import {
  ApiError,
  deleteRecord,
  getRecord,
  getTemplates,
  messageOf,
  requestSecret,
  restoreFromTrash,
  type FieldType,
  type RecordData,
  type RecordField,
  type Session,
  type Template,
  type TrashItem,
} from './api.ts';
import { useChanges } from './changes.ts';
import { CodeBlock } from './CodeBlock.tsx';
import { CopyButton } from './CopyButton.tsx';
import { FieldValue } from './FieldValue.tsx';
import { localDateTime, recordPath } from './fields.ts';
import { offersDelete, offersTrashRestore } from './history.ts';
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
  | { state: 'deleted'; item: TrashItem; template: Template | undefined }
  | { state: 'notThere' }
  | { state: 'failed'; message: string };

/**
 * A record at `/records/<id>` (M6.5, design 8.2): its header, its fields in the order of the template with "Copy" on
 * each value, the padlocks of the levels that the session does not hold, and "Show" and "Copy" of a `secret`; "History",
 * and "Delete" for an editor who holds every level of the record (M6.14). After a delete it says that the record is in
 * the trash, with "Restore". `App.tsx` mounts it again for another `id` and when the session signs in or out or its
 * levels change.
 *
 * It reads the record again at each change of other users (`useChanges()`), and keeps the last one on the screen
 * meanwhile. A shown `secret` hides again after its time, on leaving the page, at a `session` event, and when the page
 * shows another version of the record or none, as after a read that failed (M6.16); a read that gives the same version
 * keeps it.
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
    const abort = new AbortController();
    Promise.all([getRecord(id, abort.signal), getTemplates(abort.signal)]).then(
      ([record, templates]) => {
        if (!abort.signal.aborted) {
          const template = templates.templates.find((entry) => entry.id === record.template);
          secrets.showsRecord(record);
          setRead({ state: 'ready', record, template });
        }
      },
      (error: unknown) => {
        if (abort.signal.aborted) {
          return;
        }

        secrets.showsRecord(null);
        // The read after a delete of this page finds no record: the page keeps saying that it is in the trash.
        if (isNotThere(error)) {
          setRead((previous) => (previous.state === 'deleted' ? previous : { state: 'notThere' }));
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
    case 'deleted':
      return (
        <DeletedView
          item={read.item}
          session={session}
          onRestored={(record) => {
            secrets.showsRecord(record);
            setRead({ state: 'ready', record, template: read.template });
          }}
          onGone={() => setRound((value) => value + 1)}
        />
      );
    case 'ready':
      return (
        <RecordView
          record={read.record}
          template={read.template}
          session={session}
          secrets={secrets}
          onDeleted={(item) => {
            secrets.showsRecord(null);
            setRead({ state: 'deleted', item, template: read.template });
          }}
          onReload={(record) => {
            secrets.showsRecord(record);
            setRead({ state: 'ready', record, template: read.template });
          }}
          onGone={() => {
            secrets.showsRecord(null);
            setRead({ state: 'notThere' });
          }}
        />
      );
  }
}

/** What "Delete" shows below the header: nothing, the question, the request, a conflict, or why it was refused. */
type Deleting =
  | { state: 'none' }
  | { state: 'confirm' }
  | { state: 'sending' }
  | { state: 'conflict'; current: RecordData }
  | { state: 'refused'; message: string };

function RecordView({
  record,
  template,
  session,
  secrets,
  onDeleted,
  onReload,
  onGone,
}: {
  record: RecordData;
  template: Template | undefined;
  session: Session;
  secrets: ShownSecrets;
  onDeleted: (item: TrashItem) => void;
  /** Shows the record of a conflict, without a new request. */
  onReload: (record: RecordData) => void;
  onGone: () => void;
}) {
  const store = useSessionStore();
  const [deleting, setDeleting] = useState<Deleting>({ state: 'none' });
  const path = pathParts(record.stash);

  // Over the version that the page shows; a newer one is a conflict, with the record as it is now (docs/api.md).
  const sendDelete = () => {
    if (deleting.state === 'sending') {
      return;
    }

    setDeleting({ state: 'sending' });
    deleteRecord(record.id, record.version).then(onDeleted, (error: unknown) => {
      if (error instanceof ApiError && error.current !== null) {
        setDeleting({ state: 'conflict', current: error.current });
      } else if (isNotThere(error)) {
        onGone();
      } else {
        setDeleting({ state: 'refused', message: messageOf(error) });
        if (readsSession(error)) {
          store.reload();
        }
      }
    });
  };

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
        <div className="buttons">
          {offersEdit(session, record) && (
            <Link className="button" to={`${recordPath(record.id)}/edit`}>
              {texts.record.edit}
            </Link>
          )}
          <Link className="button" to={`${recordPath(record.id)}/history`}>
            {texts.history.historyLink}
          </Link>
          {offersDelete(session, record) && (
            <button
              type="button"
              onClick={() => setDeleting({ state: 'confirm' })}
              disabled={deleting.state !== 'none' && deleting.state !== 'refused'}
            >
              {texts.history.delete}
            </button>
          )}
        </div>
      </header>
      {deleting.state !== 'none' && (
        <DeletePanel
          deleting={deleting}
          onSend={sendDelete}
          onCancel={() => setDeleting({ state: 'none' })}
          onReload={(current) => {
            setDeleting({ state: 'none' });
            onReload(current);
          }}
        />
      )}
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

/** The question of "Delete", its request, a conflict with "Reload", or why it was refused. */
function DeletePanel({
  deleting,
  onSend,
  onCancel,
  onReload,
}: {
  deleting: Exclude<Deleting, { state: 'none' }>;
  onSend: () => void;
  onCancel: () => void;
  onReload: (current: RecordData) => void;
}) {
  switch (deleting.state) {
    case 'confirm':
    case 'sending':
      return (
        <div className="panel stack" role="group" aria-label={texts.history.delete}>
          <p>{texts.history.deleteConfirm}</p>
          <div className="buttons">
            <button type="button" onClick={onSend} disabled={deleting.state === 'sending'}>
              {deleting.state === 'sending' ? texts.history.deleting : texts.history.delete}
            </button>
            <button type="button" onClick={onCancel} disabled={deleting.state === 'sending'}>
              {texts.history.cancel}
            </button>
          </div>
        </div>
      );
    case 'conflict':
      return (
        <div className="panel stack" role="alert">
          <p>
            {texts.history.conflict(authorName(deleting.current.updatedBy), localDateTime(deleting.current.updatedAt))}
          </p>
          <div className="buttons">
            <button type="button" onClick={() => onReload(deleting.current)}>
              {texts.history.reload}
            </button>
            <button type="button" onClick={onCancel}>
              {texts.history.cancel}
            </button>
          </div>
        </div>
      );
    case 'refused':
      return (
        <p className="banner banner-danger" role="alert">
          {deleting.message}
        </p>
      );
  }
}

/**
 * A record that this page has moved to the trash: so it says, with "Restore" for an editor who holds every level of
 * it, which brings it back as it was deleted. A record that another Kiler has restored meanwhile (404) is read again.
 */
function DeletedView({
  item,
  session,
  onRestored,
  onGone,
}: {
  item: TrashItem;
  session: Session;
  onRestored: (record: RecordData) => void;
  onGone: () => void;
}) {
  const store = useSessionStore();
  const [restoring, setRestoring] = useState(false);
  const [refusal, setRefusal] = useState<string | null>(null);
  const restore = () => {
    if (restoring) {
      return;
    }

    setRestoring(true);
    setRefusal(null);
    restoreFromTrash(item.id).then(onRestored, (error: unknown) => {
      setRestoring(false);
      setRefusal(messageOf(error));
      if (readsSession(error)) {
        store.reload();
      } else if (isNotThere(error)) {
        onGone();
      }
    });
  };

  return (
    <section className="card stack">
      <p className="banner banner-ok" role="status">
        {texts.history.deleted(item.name)}
      </p>
      {refusal !== null && (
        <p className="banner banner-danger" role="alert">
          {refusal}
        </p>
      )}
      <div className="buttons">
        {offersTrashRestore(session, item) && (
          <button type="button" onClick={restore} disabled={restoring}>
            {restoring ? texts.history.restoring : texts.history.restore}
          </button>
        )}
        {session.signedIn && (
          <Link className="button" to="/trash">
            {texts.history.toTrash}
          </Link>
        )}
        <Link className="button" to="/">
          {texts.record.toMain}
        </Link>
      </div>
    </section>
  );
}

/** A 404 of Kiler: the record is not there, or is in the trash. */
function isNotThere(error: unknown): boolean {
  return error instanceof ApiError && error.kind === 'refused' && error.status === 404;
}

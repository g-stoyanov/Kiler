import { useEffect, useId, useReducer, useRef, useState, type FormEvent } from 'react';
import { deleteStash, getAdminStashes, mergeStash, messageOf, renameStash, type AdminStash } from './api.ts';
import { afterAdminRefusal } from './admin.ts';
import { AdminRefusal } from './AdminRefusal.tsx';
import { useChanges } from './changes.ts';
import { Field } from './Field.tsx';
import { useSessionStore } from './session.ts';
import {
  afterStashRefusal,
  checkStashName,
  mergeChoices,
  newStashesState,
  offersDelete,
  panelStash,
  stashesReducer,
  type StashPanel,
} from './stashes.ts';
import './Stashes.css';
import { texts } from './texts.ts';

type PanelKind = Exclude<StashPanel, { kind: 'none' }>['kind'];

/**
 * The stashes at `/stashes` (M6.23, design 6.6 and 8.4): the live stashes by name with the shared records of each, and on
 * each "Rename", "Merge into…" another live stash, and "Delete" while no shared record is in it. Each action sends the
 * version that its panel was opened with and answers with the list after it. The list is read again at each change of
 * the data, as the records of other users change the counts, and after a refusal of a stash that has changed or gone
 * meanwhile, or of an action that cannot be done now (404, 409).
 */
export function Stashes() {
  const store = useSessionStore();
  const changes = useChanges();
  const [state, dispatch] = useReducer(stashesReducer, newStashesState);
  // A new value reads the list again; the last list shows meanwhile.
  const [round, setRound] = useState(0);
  const readAgain = () => {
    dispatch({ type: 'reading' });
    setRound((value) => value + 1);
  };

  useEffect(() => {
    const abort = new AbortController();
    getAdminStashes(abort.signal).then(
      (stashes) => {
        if (!abort.signal.aborted) {
          dispatch({ type: 'read', stashes });
        }
      },
      // As on the other admin screens, a refused read only says why.
      (error: unknown) => {
        if (!abort.signal.aborted) {
          const signInAgain = afterAdminRefusal(error) === 'signInAgain';
          dispatch({ type: 'readFailed', message: messageOf(error), signInAgain });
        }
      },
    );
    return () => abort.abort();
  }, [changes, round]);

  const act = (request: () => Promise<AdminStash[]>, done: string) => {
    if (state.sending) {
      return;
    }

    dispatch({ type: 'send' });
    request().then(
      (stashes) => dispatch({ type: 'done', stashes, message: done }),
      (error: unknown) => {
        const after = afterStashRefusal(error);
        dispatch({ type: 'refused', message: messageOf(error), signInAgain: after === 'signInAgain' });
        if (after === 'readSession') {
          store.reload();
        } else if (after === 'readList') {
          readAgain();
        }
      },
    );
  };

  const list = state.list;
  if (list.state === 'reading') {
    return <p className="muted">{texts.stashes.reading}</p>;
  }

  if (list.state === 'failed') {
    return (
      <section className="card stack">
        <h1>{texts.stashes.title}</h1>
        <AdminRefusal message={list.message} signInAgain={list.signInAgain} />
        <div>
          <button type="button" onClick={readAgain}>
            {texts.admin.tryAgain}
          </button>
        </div>
      </section>
    );
  }

  const { stashes } = list;
  const panel = state.panel;
  const chosen = panelStash(stashes, panel);
  const close = () => dispatch({ type: 'open', panel: { kind: 'none' } });
  const t = texts.stashes;
  const panelProps = { sending: state.sending, refused: state.refusal !== undefined, onClose: close };

  return (
    <section className="card card-wide stack">
      <h1>{t.title}</h1>
      <p className="muted">{t.intro}</p>
      {state.done !== undefined && (
        <p className="banner banner-ok" role="status">
          {state.done}
        </p>
      )}
      <StashTable
        stashes={stashes}
        chosen={chosen?.id}
        disabled={state.sending}
        onOpen={(kind, stash) => dispatch({ type: 'open', panel: { kind, id: stash.id, version: stash.version } })}
      />
      {chosen !== undefined && panel.kind === 'rename' && (
        <RenamePanel
          key={chosen.id}
          stash={chosen}
          stashes={stashes}
          onRename={(name) => act(() => renameStash(chosen.id, name, panel.version), t.renamed(chosen.name, name))}
          {...panelProps}
        />
      )}
      {chosen !== undefined && panel.kind === 'merge' && (
        <MergePanel
          key={chosen.id}
          stash={chosen}
          choices={mergeChoices(stashes, chosen.id)}
          onMerge={(into) => act(() => mergeStash(chosen.id, into.id, panel.version), t.merged(chosen.name, into.name))}
          {...panelProps}
        />
      )}
      {chosen !== undefined && panel.kind === 'delete' && (
        <DeletePanel
          key={chosen.id}
          stash={chosen}
          onDelete={() => act(() => deleteStash(chosen.id, panel.version), t.deleted(chosen.name))}
          {...panelProps}
        />
      )}
      {state.refusal !== undefined && (
        <AdminRefusal message={state.refusal.message} signInAgain={state.refusal.signInAgain} />
      )}
    </section>
  );
}

function StashTable({
  stashes,
  chosen,
  disabled,
  onOpen,
}: {
  stashes: AdminStash[];
  chosen: string | undefined;
  disabled: boolean;
  onOpen: (kind: PanelKind, stash: AdminStash) => void;
}) {
  const t = texts.stashes;
  if (stashes.length === 0) {
    return <p className="muted">{t.none}</p>;
  }

  return (
    <div className="table-frame">
      <table className="table stashes-table">
        <thead>
          <tr>
            <th scope="col">{t.name}</th>
            <th scope="col">{t.records}</th>
            <th scope="col">
              <span className="visually-hidden">{t.actions}</span>
            </th>
          </tr>
        </thead>
        <tbody>
          {stashes.map((stash) => (
            <tr key={stash.id} className={stash.id === chosen ? 'chosen' : undefined}>
              <td>{stash.name}</td>
              <td>{stash.records}</td>
              <td>
                <div className="buttons">
                  <button type="button" disabled={disabled} onClick={() => onOpen('rename', stash)}>
                    {t.rename}
                    <span className="visually-hidden"> {stash.name}</span>
                  </button>
                  {mergeChoices(stashes, stash.id).length > 0 && (
                    <button type="button" disabled={disabled} onClick={() => onOpen('merge', stash)}>
                      {t.merge}
                      <span className="visually-hidden"> {stash.name}</span> {t.mergeTail}
                    </button>
                  )}
                  {offersDelete(stash) && (
                    <button type="button" disabled={disabled} onClick={() => onOpen('delete', stash)}>
                      {t.delete}
                      <span className="visually-hidden"> {stash.name}</span>
                    </button>
                  )}
                </div>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

interface PanelProps {
  stash: AdminStash;
  sending: boolean;
  /** The last action was refused: its control has lost the focus, as it was disabled while the request ran. */
  refused: boolean;
  onClose: () => void;
}

/** Gives the focus back to `control` after a refusal, as `SignIn` does. */
function useFocusAfterRefusal<T extends HTMLElement>(sending: boolean, refused: boolean) {
  const control = useRef<T>(null);
  useEffect(() => {
    if (!sending && refused) {
      control.current?.focus();
    }
  }, [sending, refused]);
  return control;
}

function RenamePanel({
  stash,
  stashes,
  sending,
  refused,
  onRename,
  onClose,
}: PanelProps & { stashes: AdminStash[]; onRename: (name: string) => void }) {
  const id = useId();
  const t = texts.stashes;
  const input = useFocusAfterRefusal<HTMLInputElement>(sending, refused);
  const [typed, setTyped] = useState(stash.name);
  // The problem shows once the admin has tried to send, and follows the field from then on.
  const [checked, setChecked] = useState(false);
  const problem = checked ? checkStashName(typed, stash.id, stashes) : undefined;
  const unchanged = typed.trim() === stash.name;
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (unchanged) {
      return;
    }

    if (checkStashName(typed, stash.id, stashes) !== undefined) {
      setChecked(true);
      return;
    }

    setChecked(false);
    onRename(typed.trim());
  };

  return (
    <form className="panel stack" onSubmit={submit} noValidate>
      <h2>{t.renameTitle(stash.name)}</h2>
      <Field id={`${id}-name`} label={t.newName} problem={problem}>
        <input
          ref={input}
          id={`${id}-name`}
          autoComplete="off"
          value={typed}
          disabled={sending}
          onChange={(event) => setTyped(event.target.value)}
          autoFocus
        />
      </Field>
      <div className="buttons">
        <button type="submit" className="primary" disabled={sending || unchanged}>
          {sending ? t.renaming : t.rename}
        </button>
        <button type="button" onClick={onClose} disabled={sending}>
          {t.cancel}
        </button>
      </div>
    </form>
  );
}

function MergePanel({
  stash,
  choices,
  sending,
  refused,
  onMerge,
  onClose,
}: PanelProps & { choices: AdminStash[]; onMerge: (into: AdminStash) => void }) {
  const id = useId();
  const t = texts.stashes;
  const select = useFocusAfterRefusal<HTMLSelectElement>(sending, refused);
  const [into, setInto] = useState('');
  // A choice that a new list no longer has is no choice.
  const chosen = choices.find((choice) => choice.id === into);
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (chosen !== undefined) {
      onMerge(chosen);
    }
  };

  return (
    <form className="panel stack" onSubmit={submit} noValidate>
      <h2>{t.mergeTitle(stash.name)}</h2>
      <p>{t.mergeIntro(stash.name)}</p>
      <Field id={`${id}-into`} label={t.into} problem={undefined}>
        <select
          ref={select}
          id={`${id}-into`}
          value={chosen?.id ?? ''}
          disabled={sending}
          onChange={(event) => setInto(event.target.value)}
          autoFocus
        >
          <option value="" disabled>
            {t.chooseInto}
          </option>
          {choices.map((choice) => (
            <option key={choice.id} value={choice.id}>
              {choice.name}
            </option>
          ))}
        </select>
      </Field>
      <div className="buttons">
        <button type="submit" className="primary" disabled={sending || chosen === undefined}>
          {sending ? t.merging : t.merge}
        </button>
        <button type="button" onClick={onClose} disabled={sending}>
          {t.cancel}
        </button>
      </div>
    </form>
  );
}

/** The question before a delete; "Cancel" has the focus at first. */
function DeletePanel({ stash, sending, refused, onDelete, onClose }: PanelProps & { onDelete: () => void }) {
  const id = useId();
  const t = texts.stashes;
  const button = useFocusAfterRefusal<HTMLButtonElement>(sending, refused);
  return (
    <div className="panel stack" role="group" aria-labelledby={`${id}-title`}>
      <h2 id={`${id}-title`}>{t.deleteTitle(stash.name)}</h2>
      <p>{t.deleteIntro}</p>
      <div className="buttons">
        <button ref={button} type="button" className="primary" onClick={onDelete} disabled={sending}>
          {sending ? t.deleting : t.delete}
        </button>
        <button type="button" onClick={onClose} disabled={sending} autoFocus>
          {t.cancel}
        </button>
      </div>
    </div>
  );
}

import { useEffect, useId, useState, type FormEvent } from 'react';
import { getAdminLevels, messageOf, renameLevel, type AdminLevel } from './api.ts';
import { afterAdminRefusal, checkLevelName } from './admin.ts';
import { AdminRefusal } from './AdminRefusal.tsx';
import { Field } from './Field.tsx';
import { useSessionStore } from './session.ts';
import { texts } from './texts.ts';

type LevelsList =
  | { state: 'reading' }
  | { state: 'ready'; levels: AdminLevel[] }
  | { state: 'failed'; message: string; signInAgain: boolean };

interface Refusal {
  message: string;
  signInAgain: boolean;
}

/**
 * The levels of `levels.json` (M4.9): each gets a new name here, one rename at a time. A new level needs the recovery
 * key and new temporary passwords, so it comes after the exam (decisions of M4).
 */
export function Levels() {
  const store = useSessionStore();
  const [list, setList] = useState<LevelsList>({ state: 'reading' });
  // A new value reads the levels again; the last ones show meanwhile.
  const [round, setRound] = useState(0);
  const [sending, setSending] = useState<number | undefined>(undefined);
  const [refusal, setRefusal] = useState<Refusal | undefined>(undefined);
  const [renamed, setRenamed] = useState<number | undefined>(undefined);
  const readAgain = () => {
    setList((last) => (last.state === 'failed' ? { state: 'reading' } : last));
    setRound((value) => value + 1);
  };

  useEffect(() => {
    const abort = new AbortController();
    getAdminLevels(abort.signal).then(
      (levels) => {
        if (!abort.signal.aborted) {
          setList({ state: 'ready', levels });
        }
      },
      // As on the users screen, a refused read only says why.
      (error: unknown) => {
        if (!abort.signal.aborted) {
          setList({
            state: 'failed',
            message: messageOf(error),
            signInAgain: afterAdminRefusal(error) === 'signInAgain',
          });
        }
      },
    );
    return () => abort.abort();
  }, [round]);

  const rename = (level: number, name: string) => {
    if (sending !== undefined) {
      return;
    }

    setSending(level);
    setRefusal(undefined);
    setRenamed(undefined);
    renameLevel(level, name).then(
      (levels) => {
        setSending(undefined);
        setList({ state: 'ready', levels });
        setRenamed(level);
      },
      (error: unknown) => {
        const after = afterAdminRefusal(error);
        setSending(undefined);
        setRefusal({ message: messageOf(error), signInAgain: after === 'signInAgain' });
        if (after === 'readSession') {
          store.reload();
        } else if (after === 'readList') {
          readAgain();
        }
      },
    );
  };

  if (list.state === 'reading') {
    return <p className="muted">{texts.admin.reading}</p>;
  }

  if (list.state === 'failed') {
    return (
      <section className="card stack">
        <h1>{texts.levels.title}</h1>
        <AdminRefusal message={list.message} signInAgain={list.signInAgain} />
        <div>
          <button type="button" onClick={readAgain}>
            {texts.admin.tryAgain}
          </button>
        </div>
      </section>
    );
  }

  return (
    <section className="card stack">
      <h1>{texts.levels.title}</h1>
      <p className="muted">{texts.levels.intro}</p>
      {renamed !== undefined && (
        <p className="banner banner-ok" role="status">
          {texts.levels.renamed(renamed)}
        </p>
      )}
      {list.levels.map((entry) => (
        // A new name makes a new row, so it starts from the name that Kiler has.
        <LevelRow
          key={`${String(entry.level)}:${entry.name}`}
          entry={entry}
          levels={list.levels}
          sending={sending}
          onRename={rename}
        />
      ))}
      {refusal !== undefined && <AdminRefusal message={refusal.message} signInAgain={refusal.signInAgain} />}
    </section>
  );
}

function LevelRow({
  entry,
  levels,
  sending,
  onRename,
}: {
  entry: AdminLevel;
  levels: AdminLevel[];
  sending: number | undefined;
  onRename: (level: number, name: string) => void;
}) {
  const id = useId();
  const [typed, setTyped] = useState(entry.name);
  // The problem shows once the admin has tried to send, and follows the field from then on.
  const [checked, setChecked] = useState(false);
  const problem = checked ? checkLevelName(typed, entry.level, levels) : undefined;
  const unchanged = typed.trim() === entry.name;
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (unchanged) {
      return;
    }

    if (checkLevelName(typed, entry.level, levels) !== undefined) {
      setChecked(true);
      return;
    }

    setChecked(false);
    onRename(entry.level, typed.trim());
  };

  return (
    <form className="level-row" onSubmit={submit} noValidate>
      <Field id={`${id}-name`} label={texts.levels.name(entry.level)} problem={problem}>
        <input
          id={`${id}-name`}
          autoComplete="off"
          value={typed}
          disabled={sending !== undefined}
          onChange={(event) => setTyped(event.target.value)}
        />
      </Field>
      <button type="submit" disabled={sending !== undefined || unchanged}>
        {sending === entry.level ? texts.levels.renaming : texts.levels.rename}
      </button>
    </form>
  );
}

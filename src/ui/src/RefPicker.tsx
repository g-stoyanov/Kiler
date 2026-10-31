import { useEffect, useId, useState, type KeyboardEvent } from 'react';
import { ApiError, getRecords, messageOf, type ListItem, type RefValue, type TemplateField } from './api.ts';
import { useChanges } from './changes.ts';
import { refQuery } from './recordForm.ts';
import { useSessionStore } from './session.ts';
import { texts } from './texts.ts';

const t = texts.form;

/** How long the picker waits after the last key before it searches, as the main view does. */
const searchDelay = 150;

type Found =
  | { state: 'idle' }
  | { state: 'reading' }
  | { state: 'ready'; records: ListItem[] }
  | { state: 'failed'; message: string };

/**
 * The record that a `ref` points to (M6.8): the one chosen, with "Clear", or a search of the records of `refTemplate`
 * by their name (`GET /api/records?template=<refTemplate>&q=`), only while the input has the focus, the last search
 * counting. A record that has gone says so. `id` is on the input, or on "Clear" while a record is chosen.
 */
export function RefPicker({
  id,
  field,
  value,
  onRef,
}: {
  id: string;
  field: TemplateField;
  value: RefValue | null;
  onRef: (ref: RefValue | null) => void;
}) {
  const store = useSessionStore();
  const changes = useChanges();
  const listId = useId();
  const [typed, setTyped] = useState('');
  const [query, setQuery] = useState('');
  const [expanded, setExpanded] = useState(false);
  const [active, setActive] = useState(-1);
  const [found, setFound] = useState<Found>({ state: 'idle' });

  useEffect(() => {
    const timer = setTimeout(() => setQuery(typed), searchDelay);
    return () => clearTimeout(timer);
  }, [typed]);

  useEffect(() => {
    if (!expanded) {
      return;
    }

    const abort = new AbortController();
    getRecords(refQuery(field, query), abort.signal).then(
      (answer) => {
        if (!abort.signal.aborted) {
          setFound({ state: 'ready', records: answer.records });
        }
      },
      (error: unknown) => {
        if (!abort.signal.aborted) {
          setFound({ state: 'failed', message: messageOf(error) });
          if (error instanceof ApiError && error.kind === 'refused' && error.status === 403) {
            store.reload();
          }
        }
      },
    );
    return () => abort.abort();
  }, [expanded, field, query, changes, store]);

  if (value !== null) {
    return (
      <div className="form-ref">
        <span className={value.name === null ? 'field-unreadable' : undefined}>{value.name ?? t.refGone}</span>
        <button type="button" id={id} aria-label={t.refClearLabel(field.label)} onClick={() => onRef(null)}>
          {t.refClear}
        </button>
      </div>
    );
  }

  const records = found.state === 'ready' ? found.records : [];
  const choose = (record: ListItem) => {
    setExpanded(false);
    setActive(-1);
    setTyped('');
    onRef({ id: record.id, name: record.name });
  };

  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        setExpanded(true);
        setActive((index) => Math.min(index + 1, records.length - 1));
        break;
      case 'ArrowUp':
        event.preventDefault();
        setActive((index) => Math.max(index - 1, 0));
        break;
      case 'Enter': {
        const record = expanded ? records[active] : undefined;
        if (record !== undefined) {
          event.preventDefault();
          choose(record);
        } else if (typed.trim() !== '') {
          // A search half typed never sends the form.
          event.preventDefault();
        }

        break;
      }
      case 'Escape':
        if (expanded) {
          event.preventDefault();
          setExpanded(false);
          setActive(-1);
        }

        break;
    }
  };

  const activeRecord = expanded ? records[active] : undefined;
  return (
    <div className="form-combobox">
      <input
        id={id}
        role="combobox"
        aria-autocomplete="list"
        aria-expanded={expanded && records.length > 0}
        aria-controls={listId}
        aria-activedescendant={activeRecord === undefined ? undefined : `${listId}-${String(active)}`}
        autoComplete="off"
        placeholder={t.refSearch}
        value={typed}
        onChange={(event) => {
          setTyped(event.target.value);
          setExpanded(true);
          setActive(-1);
        }}
        onFocus={() => {
          setFound((last) => (last.state === 'idle' ? { state: 'reading' } : last));
          setExpanded(true);
        }}
        onBlur={() => {
          setExpanded(false);
          setActive(-1);
        }}
        onKeyDown={onKeyDown}
      />
      {expanded && records.length > 0 && (
        <ul id={listId} role="listbox" className="form-options" aria-label={field.label}>
          {records.map((record, index) => (
            <li
              key={record.id}
              id={`${listId}-${String(index)}`}
              role="option"
              aria-selected={index === active}
              className="form-option"
              onMouseDown={(event) => event.preventDefault()}
              onClick={() => choose(record)}
            >
              {record.name}
            </li>
          ))}
        </ul>
      )}
      {expanded && found.state === 'reading' && (
        <p className="muted" role="status">
          {t.refReading}
        </p>
      )}
      {expanded && found.state === 'ready' && records.length === 0 && <p className="muted">{t.refNone}</p>}
      {found.state === 'failed' && (
        <p className="field-problem" role="alert">
          {found.message}
        </p>
      )}
    </div>
  );
}

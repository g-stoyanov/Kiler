import { useEffect, useId, useRef, useState, type Dispatch, type KeyboardEvent } from 'react';
import { ApiError, createStash, getStashes, messageOf } from './api.ts';
import { useChanges } from './changes.ts';
import { useSessionStore } from './session.ts';
import {
  maxStashes,
  optionsOf,
  positionsOf,
  type PathEntry,
  type StashOption,
  type StashPickerAction,
  type StashPickerState,
} from './stashPicker.ts';
import { texts } from './texts.ts';
import './StashPicker.css';

const t = texts.form.stashes;

/**
 * The stash path of a record (M6.6; design 6.6, "The form"): four drop-downs, each of which filters as the user types
 * and offers "Create …" for a name that no stash has. A position opens once the one before it is chosen, and "Clear"
 * clears it and those after it. The positions of a "+" are locked until "Change", and the focus starts at the next one.
 *
 * The form holds the state ({@link stashPickerReducer} of `stashPicker.ts`), as its request needs the path. The picker
 * reads the stashes for the open position, again at the changes of others, and sends a new stash, one at a time. `id`
 * goes on the control that takes the focus, such as after a 400 of `stash`: the open position, else the last control.
 */
export function StashPicker({
  id,
  state,
  dispatch,
  disabled,
}: {
  id: string;
  state: StashPickerState;
  dispatch: Dispatch<StashPickerAction>;
  disabled: boolean;
}) {
  const store = useSessionStore();
  const changes = useChanges();
  const listId = useId();
  const [expanded, setExpanded] = useState(false);
  const [active, setActive] = useState(-1);
  const target = useRef<HTMLElement | null>(null);
  const setTarget = (element: HTMLElement | null) => {
    target.current = element;
  };
  // From a "+", the focus starts at the next position; after a choice or a clear, it goes to the new open position.
  const moveFocus = useRef(state.locked > 0);
  const sending = useRef(false);
  const pathKey = state.path.map((entry) => entry.id).join(',');

  useEffect(() => {
    const abort = new AbortController();
    const ids = pathKey === '' ? [] : pathKey.split(',');
    getStashes(ids.slice(0, maxStashes - 1), abort.signal).then(
      (stashes) => {
        if (!abort.signal.aborted) {
          dispatch({ type: 'read', stashes });
        }
      },
      (error: unknown) => {
        if (!abort.signal.aborted) {
          dispatch({ type: 'readFailed', message: messageOf(error) });
          if (isForbidden(error)) {
            store.reload();
          }
        }
      },
    );
    return () => abort.abort();
  }, [pathKey, state.reads, changes, dispatch, store]);

  useEffect(() => {
    if (moveFocus.current) {
      moveFocus.current = false;
      target.current?.focus();
    }
  }, [pathKey]);

  const options = optionsOf(state);
  const busy = state.creating !== undefined || state.pending !== undefined;
  const positions = positionsOf(state);
  const last = positions.findLastIndex((view) => view.kind !== 'closed');

  const close = () => {
    setExpanded(false);
    setActive(-1);
  };

  const create = (name: string) => {
    if (sending.current) {
      return;
    }

    sending.current = true;
    dispatch({ type: 'create' });
    createStash(name)
      .then(
        (stash) => {
          moveFocus.current = true;
          dispatch({ type: 'created', stash });
        },
        (error: unknown) => {
          const refused = error instanceof ApiError && error.kind === 'refused';
          // A 409 reads the stashes again, and chooses the stash of that name when it is there.
          moveFocus.current = refused && error.status === 409;
          dispatch({ type: 'createRefused', message: messageOf(error), status: refused ? error.status : undefined });
          if (isForbidden(error)) {
            store.reload();
          }
        },
      )
      .finally(() => {
        sending.current = false;
      });
  };

  const pick = (option: StashOption) => {
    close();
    if (option.kind === 'create') {
      create(option.name);
      return;
    }

    moveFocus.current = true;
    dispatch({ type: 'choose', stash: option.stash });
  };

  const clear = (position: number) => {
    moveFocus.current = true;
    dispatch({ type: 'clear', position });
  };

  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        setExpanded(true);
        setActive((index) => Math.min(index + 1, options.length - 1));
        break;
      case 'ArrowUp':
        event.preventDefault();
        setActive((index) => Math.max(index - 1, 0));
        break;
      case 'Enter': {
        const option = expanded ? options[active] : undefined;
        if (option !== undefined) {
          event.preventDefault();
          pick(option);
        } else if (state.typed.trim() !== '') {
          // A name half typed never sends the form; an empty position does, as the record stays before it.
          event.preventDefault();
        }

        break;
      }
      case 'Escape':
        if (expanded) {
          event.preventDefault();
          close();
        }

        break;
    }
  };

  const activeOption = expanded ? options[active] : undefined;
  return (
    <fieldset className="stash-picker" disabled={disabled}>
      <legend>{t.legend}</legend>
      <p className="muted">{state.locked > 0 ? t.locked : t.hint}</p>
      <ol className="stash-positions">
        {positions.map((view, position) => {
          const number = position + 1;
          const isTarget = position === last;
          return (
            <li key={position} className="stash-position">
              <span className="stash-number" aria-hidden="true">
                {number}
              </span>
              {view.kind === 'locked' && (
                <>
                  <span className="stash-chosen stash-locked">{nameOf(view.entry)}</span>
                  {position === state.locked - 1 && (
                    <button
                      type="button"
                      id={isTarget ? id : undefined}
                      ref={isTarget ? setTarget : undefined}
                      onClick={() => dispatch({ type: 'change' })}
                    >
                      {t.change}
                    </button>
                  )}
                </>
              )}
              {view.kind === 'chosen' && (
                <>
                  <span className="stash-chosen">{nameOf(view.entry)}</span>
                  <button
                    type="button"
                    id={isTarget ? id : undefined}
                    ref={isTarget ? setTarget : undefined}
                    aria-label={t.clearLabel(number, nameOf(view.entry))}
                    disabled={busy}
                    onClick={() => clear(position)}
                  >
                    {t.clear}
                  </button>
                </>
              )}
              {view.kind === 'open' && (
                <div className="stash-combobox">
                  <input
                    id={id}
                    ref={setTarget}
                    role="combobox"
                    aria-label={t.position(number)}
                    aria-autocomplete="list"
                    aria-expanded={expanded && options.length > 0}
                    aria-controls={listId}
                    aria-activedescendant={activeOption === undefined ? undefined : `${listId}-${String(active)}`}
                    autoComplete="off"
                    placeholder={position === 0 ? t.first : t.next}
                    value={state.typed}
                    readOnly={busy}
                    onChange={(event) => {
                      dispatch({ type: 'type', text: event.target.value });
                      setExpanded(true);
                      setActive(event.target.value.trim() === '' ? -1 : 0);
                    }}
                    onFocus={() => setExpanded(true)}
                    onBlur={close}
                    onKeyDown={onKeyDown}
                  />
                  {expanded && options.length > 0 && (
                    <ul id={listId} role="listbox" className="stash-options" aria-label={t.position(number)}>
                      {options.map((option, index) => (
                        <li
                          key={option.kind === 'stash' ? option.stash.id : ''}
                          id={`${listId}-${String(index)}`}
                          role="option"
                          aria-selected={index === active}
                          className={option.kind === 'create' ? 'stash-option stash-create' : 'stash-option'}
                          // The input keeps the focus, so its blur does not close the list before the click.
                          onMouseDown={(event) => event.preventDefault()}
                          onClick={() => pick(option)}
                        >
                          {option.kind === 'stash' ? option.stash.name : t.create(option.name)}
                        </li>
                      ))}
                    </ul>
                  )}
                  <OpenStatus state={state} expanded={expanded} empty={options.length === 0} dispatch={dispatch} />
                </div>
              )}
              {view.kind === 'closed' && (
                <input aria-label={t.position(number)} placeholder={t.closed} disabled className="stash-closed" />
              )}
            </li>
          );
        })}
      </ol>
      {state.cut && (
        <p className="banner banner-warning" role="status">
          {t.cut}
        </p>
      )}
      {state.error !== undefined && (
        <p className="banner banner-danger" role="alert">
          {state.error}
        </p>
      )}
    </fieldset>
  );
}

/** What the open position says under its input: the read, its failure, a new stash under way, or no option. */
function OpenStatus({
  state,
  expanded,
  empty,
  dispatch,
}: {
  state: StashPickerState;
  expanded: boolean;
  empty: boolean;
  dispatch: Dispatch<StashPickerAction>;
}) {
  if (state.creating !== undefined) {
    return (
      <p className="muted" role="status">
        {t.creating(state.creating)}
      </p>
    );
  }

  if (state.choices.state === 'failed') {
    return (
      <p className="field-problem" role="alert">
        {state.choices.message}{' '}
        <button type="button" onClick={() => dispatch({ type: 'readAgain' })}>
          {t.tryAgain}
        </button>
      </p>
    );
  }

  if (!expanded) {
    return null;
  }

  if (state.choices.state === 'reading') {
    return (
      <p className="muted" role="status">
        {t.reading}
      </p>
    );
  }

  return empty ? <p className="muted">{state.typed.trim() === '' ? t.none : t.noMatch}</p> : null;
}

function nameOf(entry: PathEntry): string {
  return entry.name ?? t.unnamed;
}

/** A 403, after which the screen reads the session again (kiler-ui, "API"). */
function isForbidden(error: unknown): boolean {
  return error instanceof ApiError && error.kind === 'refused' && error.status === 403;
}

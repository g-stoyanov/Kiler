import { useCallback, useEffect, useId, useReducer, useRef, useState, type FormEvent } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import {
  ApiError,
  createRecord,
  editRecord,
  getLevels,
  getRecord,
  getTemplates,
  messageOf,
  recordsSearch,
  type LevelName,
  type RecordData,
  type Session,
  type Template,
} from './api.ts';
import { Field } from './Field.tsx';
import { FieldInput } from './FieldInput.tsx';
import { localDateTime, recordPath } from './fields.ts';
import {
  checkDraft,
  conflictKey,
  descriptionKey,
  editDraft,
  envKey,
  fieldViews,
  newDraft,
  newRecordFormState,
  overwriteRequest,
  recordFormReducer,
  recordRequest,
  suggestedEnv,
  type Draft,
} from './recordForm.ts';
import { useSessionStore } from './session.ts';
import { StashPicker } from './StashPicker.tsx';
import {
  newStashPickerState,
  pathOfAddress,
  stashPickerReducer,
  startOfNode,
  startOfRecord,
  type StashPickerAction,
  type StashPickerStart,
} from './stashPicker.ts';
import { TagsInput } from './TagsInput.tsx';
import { texts } from './texts.ts';
import './RecordForm.css';

const t = texts.form;

type Load =
  | { state: 'reading' }
  | { state: 'failed'; message: string }
  | { state: 'ready'; templates: Template[]; levels: LevelName[]; record: RecordData | null };

/**
 * The form of a record (M6.7; design 6.6, "The form"): a new one at `/new`, with the path of `?stash=` from a "+", and
 * the edit of the record `id` at `/records/<id>/edit`. Only a session with `canEdit` reaches it (`App.tsx`), which
 * mounts it again for another `id`. It reads the templates and the levels, and the record of an edit, then shows the
 * form, generated from the template.
 */
export function RecordForm({ id, session }: { id?: string; session: Session }) {
  const store = useSessionStore();
  const [params] = useSearchParams();
  const stash = params.get('stash');
  const [load, setLoad] = useState<Load>({ state: 'reading' });
  // A new value reads again, after a read that failed.
  const [round, setRound] = useState(0);

  useEffect(() => {
    const abort = new AbortController();
    Promise.all([
      getTemplates(abort.signal),
      getLevels(abort.signal),
      id === undefined ? null : getRecord(id, abort.signal),
    ]).then(
      ([templates, levels, record]) => {
        if (!abort.signal.aborted) {
          setLoad({ state: 'ready', templates: templates.templates, levels, record });
        }
      },
      (error: unknown) => {
        if (!abort.signal.aborted) {
          setLoad({ state: 'failed', message: messageOf(error) });
          if (isForbidden(error)) {
            store.reload();
          }
        }
      },
    );
    return () => abort.abort();
  }, [id, round, store]);

  const title = id === undefined ? t.newTitle : t.editTitle;
  const back = id === undefined ? `/${recordsSearch({ stash: pathOfAddress(stash) })}` : recordPath(id);
  if (load.state !== 'ready') {
    return (
      <section className="card stack">
        <h1>{title}</h1>
        {load.state === 'reading' ? (
          <p className="muted" role="status">
            {id === undefined ? t.reading : t.readingRecord}
          </p>
        ) : (
          <>
            <p className="banner banner-danger" role="alert">
              {load.message}
            </p>
            <div className="buttons">
              <button
                type="button"
                onClick={() => {
                  setLoad({ state: 'reading' });
                  setRound((value) => value + 1);
                }}
              >
                {t.tryAgain}
              </button>
              <Link className="button" to="/">
                {t.toBrowse}
              </Link>
            </div>
          </>
        )}
      </section>
    );
  }

  const { templates, levels, record } = load;
  const template = record === null ? undefined : templates.find((entry) => entry.id === record.template);
  if (record !== null && (template === undefined || record.fields.length === 0)) {
    // Kiler refuses the edit of a record whose template is missing or does not fit (docs/api.md, "Record").
    return (
      <section className="card stack">
        <h1>{title}</h1>
        <p className="banner banner-warning">{t.cannotEdit}</p>
        <Link className="button" to={back}>
          {t.cancel}
        </Link>
      </section>
    );
  }

  if (record === null && templates.length === 0) {
    return (
      <section className="card stack">
        <h1>{title}</h1>
        <p className="banner banner-warning">{t.noTemplates}</p>
      </section>
    );
  }

  const draft = record === null || template === undefined ? newDraft(null) : editDraft(record, template);
  const start = record === null ? startOfNode(pathOfAddress(stash)) : startOfRecord(record.stash);
  return (
    <Editor
      // A new `?stash=` of `/new`, such as Back from one "+" to another, starts the form again.
      key={record === null ? `new ${stash ?? ''}` : `edit ${String(record.version)}`}
      id={id}
      title={title}
      back={back}
      draft={draft}
      start={start}
      templates={templates}
      levels={levels}
      session={session}
      // "Reload" of a conflict: the form of the record as it is now, without a new request.
      onReload={(current) => setLoad((last) => (last.state === 'ready' ? { ...last, record: current } : last))}
    />
  );
}

/** The form itself, from its draft and the start of its stash path, which it keeps while it is mounted. */
function Editor({
  id,
  title,
  back,
  draft: first,
  start,
  templates,
  levels,
  session,
  onReload,
}: {
  id: string | undefined;
  title: string;
  back: string;
  draft: Draft;
  start: StashPickerStart;
  templates: Template[];
  levels: LevelName[];
  session: Session;
  onReload: (current: RecordData) => void;
}) {
  const store = useSessionStore();
  const navigate = useNavigate();
  const base = useId();
  const [state, dispatch] = useReducer(recordFormReducer, first, newRecordFormState);
  const [picker, dispatchPicker] = useReducer(stashPickerReducer, start, newStashPickerState);
  const sending = useRef(false);
  const saveButton = useRef<HTMLButtonElement>(null);
  const top = Math.max(0, ...session.levels);
  const draft = state.draft;
  const idOf = (key: string) => `${base}-${key}`;

  // After a check or a refusal, the focus goes to the first problem, or to "Save" for a sentence of its own.
  useEffect(() => {
    if (state.round === 0) {
      return;
    }

    // A field with only its padlock has no element to focus.
    const field = state.focus === undefined ? null : document.getElementById(`${base}-${state.focus}`);
    (field ?? saveButton.current)?.focus();
  }, [state.round, state.focus, base]);

  /** A save, or the "Overwrite" of a conflict over the version of `current`. */
  const send = (current: RecordData | undefined) => {
    if (sending.current) {
      return;
    }

    const stash = picker.path.map((entry) => entry.id);
    const problems = checkDraft(draft, { ids: stash, typed: picker.typed }, top, levels);
    if (Object.keys(problems).length > 0 || draft.template === null) {
      dispatch({ type: 'problems', problems });
      return;
    }

    const request = recordRequest(draft, stash, top, levels);
    const saving =
      id === undefined || draft.start === null
        ? createRecord({ ...request, template: draft.template.id })
        : editRecord(
            id,
            current === undefined
              ? { ...request, version: draft.start.version }
              : overwriteRequest(draft, stash, top, levels, current),
          );
    sending.current = true;
    dispatch({ type: 'send', sent: Object.keys(request.fields) });
    saving.then(
      (saved) => {
        void navigate(recordPath(saved.id));
      },
      (error: unknown) => {
        sending.current = false;
        dispatch({
          type: 'refused',
          message: messageOf(error),
          field: error instanceof ApiError ? error.field : null,
          current: error instanceof ApiError ? error.current : null,
        });
        if (isForbidden(error)) {
          store.reload();
        }
      },
    );
  };

  const save = (event: FormEvent) => {
    event.preventDefault();
    send(undefined);
  };

  // Stable, as the picker reads the stashes again when its `dispatch` changes.
  const changePath = useCallback((action: StashPickerAction) => {
    dispatchPicker(action);
    dispatch({ type: 'touch', key: 'stash' });
  }, []);

  const views = new Map(fieldViews(draft, top, levels).map((view) => [view.field.key, view]));
  const env = suggestedEnv(draft, picker.path);
  const field = (key: string) => {
    const view = views.get(key);
    return (
      view !== undefined && (
        <FieldInput
          key={key}
          id={idOf(key)}
          view={view}
          levels={levels}
          problem={state.problems[key]}
          onValue={(value) => dispatch({ type: 'value', key, value })}
          onLevel={(level) => dispatch({ type: 'level', key, level })}
          extra={
            key === envKey &&
            env !== null && (
              <p className="form-suggestion muted">
                {t.envSuggested(env)}
                <button
                  type="button"
                  onClick={() => dispatch({ type: 'value', key, value: { kind: 'text', text: env } })}
                >
                  {t.envUse(env)}
                </button>
              </p>
            )
          }
        />
      )
    );
  };

  return (
    <form className="card card-wide stack record-form" onSubmit={save} noValidate>
      <h1>{title}</h1>
      <fieldset className="form-all" disabled={state.sending}>
        {draft.start === null ? (
          <Field id={idOf('template')} label={t.template} problem={state.problems.template}>
            <select
              id={idOf('template')}
              value={draft.template?.id ?? ''}
              onChange={(event) => {
                const chosen = templates.find((entry) => entry.id === event.target.value);
                if (chosen !== undefined) {
                  dispatch({ type: 'template', template: chosen });
                }
              }}
            >
              {draft.template === null && <option value="">{t.chooseTemplate}</option>}
              {templates.map((entry) => (
                <option key={entry.id} value={entry.id}>
                  {entry.name}
                </option>
              ))}
            </select>
          </Field>
        ) : (
          <p className="muted">
            {t.template}: {draft.template?.name}
          </p>
        )}
        <div className="stack">
          <StashPicker id={idOf('stash')} state={picker} dispatch={changePath} disabled={state.sending} />
          {state.problems.stash !== undefined && <span className="field-problem">{state.problems.stash}</span>}
        </div>
        {draft.template !== null && (
          <>
            <Field id={idOf('name')} label={t.required(t.name)} problem={state.problems.name}>
              <input
                id={idOf('name')}
                value={draft.name}
                onChange={(event) => dispatch({ type: 'name', text: event.target.value })}
              />
            </Field>
            {field(descriptionKey)}
            <Field id={idOf('tags')} label={t.tags} problem={state.problems.tags}>
              <TagsInput id={idOf('tags')} tags={draft.tags} onTags={(tags) => dispatch({ type: 'tags', tags })} />
            </Field>
            {draft.template.fields.map((entry) => field(entry.key))}
          </>
        )}
        {state.conflict !== undefined && (
          <Conflict current={state.conflict} reloadId={idOf(conflictKey)} onReload={onReload} onOverwrite={send} />
        )}
        {state.error !== undefined && (
          <p className="banner banner-danger" role="alert">
            {state.error}
          </p>
        )}
        <div className="buttons">
          <button type="submit" className="primary" ref={saveButton}>
            {state.sending ? t.saving : t.save}
          </button>
          <Link className="button" to={back}>
            {t.cancel}
          </Link>
        </div>
      </fieldset>
    </form>
  );
}

/**
 * The conflict of a save (M6.8; docs/api.md, "Conflict"): who changed the record and when, with "Reload", the form of
 * the record as it is now, and "Overwrite", the edit sent again over its version. Side by side after the exam.
 */
function Conflict({
  current,
  reloadId,
  onReload,
  onOverwrite,
}: {
  current: RecordData;
  reloadId: string;
  onReload: (current: RecordData) => void;
  onOverwrite: (current: RecordData) => void;
}) {
  return (
    <div className="banner banner-warning form-conflict" role="alert">
      <p>{t.conflict(current.updatedBy.name ?? current.updatedBy.account, localDateTime(current.updatedAt))}</p>
      <p>{t.conflictChoice}</p>
      <div className="buttons">
        <button type="button" id={reloadId} onClick={() => onReload(current)}>
          {t.reload}
        </button>
        <button type="button" onClick={() => onOverwrite(current)}>
          {t.overwrite}
        </button>
      </div>
    </div>
  );
}

/** A 403, after which the screen reads the session again (kiler-ui, "API"). */
function isForbidden(error: unknown): boolean {
  return error instanceof ApiError && error.kind === 'refused' && error.status === 403;
}

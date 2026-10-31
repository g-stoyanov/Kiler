import { useEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import { useLocation, useNavigate } from 'react-router';
import {
  ApiError,
  getRecords,
  getTemplates,
  messageOf,
  recordsSearch,
  type RecordsAnswer,
  type RecordsQuery,
  type Session,
  type Template,
} from './api.ts';
import {
  browsePath,
  draftState,
  isSearchShortcut,
  newDraftId,
  pendingSearch,
  queryFromSearch,
  searchText,
  searchWait,
  typeSearch,
  type SearchDraft,
} from './browse.ts';
import './Browse.css';
import { useChanges } from './changes.ts';
import { RecordList } from './RecordList.tsx';
import { useSessionStore } from './session.ts';
import { texts } from './texts.ts';
import { Tree } from './Tree.tsx';

/** The read of the records; an answer keeps the search it is of, as it shows while the next one is read. */
type RecordsRead =
  | { state: 'reading' }
  | { state: 'read'; answer: RecordsAnswer; search: string }
  | { state: 'failed'; message: string };

type TemplatesRead =
  | { state: 'reading' }
  | { state: 'read'; templates: ReadonlyMap<string, Template> }
  | { state: 'failed'; message: string };

/**
 * The main view at `/` (M6.3, M6.4, design 8.1): the search, the stash tree and the list, with the query in the address
 * (decided at the start of M6), so a link to a node, Back and a reload keep the view. Each query is one read of
 * `GET /api/records`, where only the last one counts, and the changes of other users read it again.
 */
export function Browse({ session }: { session: Session }) {
  const location = useLocation();
  const store = useSessionStore();
  const changes = useChanges();
  // The address in the form that `recordsSearch` writes, so the same query in another form reads nothing again.
  const search = recordsSearch(queryFromSearch(location.search));
  const query = useMemo(() => queryFromSearch(search), [search]);
  const templates = useTemplates(changes);
  const [records, setRecords] = useState<RecordsRead>({ state: 'reading' });
  // A new value reads again, for "Try again" after a failure.
  const [round, setRound] = useState(0);

  useEffect(() => {
    const abort = new AbortController();
    getRecords(queryFromSearch(search), abort.signal).then(
      (answer) => {
        if (!abort.signal.aborted) {
          setRecords({ state: 'read', answer, search });
        }
      },
      (error: unknown) => {
        if (abort.signal.aborted) {
          return;
        }

        setRecords({ state: 'failed', message: messageOf(error) });
        // A 403 comes only from a race with a lock, so the session is read again (kiler-ui, "API").
        if (error instanceof ApiError && error.status === 403) {
          store.reload();
        }
      },
    );
    return () => abort.abort();
  }, [search, changes, round, store]);

  const canEdit = session.signedIn && session.canEdit;
  return (
    <div className="browse">
      <SearchField query={query} />
      {records.state === 'read' && (
        <aside className="browse-side">
          <Tree nodes={records.answer.tree} query={query} canEdit={canEdit} />
        </aside>
      )}
      <section className="browse-main stack">
        <h1>{texts.browse.title}</h1>
        {templates.state === 'failed' && (
          <p className="banner banner-warning" role="status">
            {texts.browse.templatesFailed(templates.message)}
          </p>
        )}
        {records.state === 'reading' && <p className="muted">{texts.browse.reading}</p>}
        {records.state === 'failed' && (
          <>
            <p className="banner banner-danger" role="alert">
              {records.message}
            </p>
            <div>
              <button
                type="button"
                onClick={() => {
                  setRecords({ state: 'reading' });
                  setRound((value) => value + 1);
                }}
              >
                {texts.browse.tryAgain}
              </button>
            </div>
          </>
        )}
        {records.state === 'read' && (
          <>
            <p className="muted" role="status">
              {records.answer.records.length === 0
                ? records.search === ''
                  ? texts.browse.empty
                  : texts.browse.none
                : texts.browse.count(records.answer.records.length)}
            </p>
            <RecordList
              records={records.answer.records}
              templates={templates.state === 'read' ? templates.templates : undefined}
            />
          </>
        )}
      </section>
    </div>
  );
}

/** The templates by id, for their names and the labels and types of their fields; read again at each change. */
function useTemplates(changes: number): TemplatesRead {
  const [templates, setTemplates] = useState<TemplatesRead>({ state: 'reading' });
  useEffect(() => {
    const abort = new AbortController();
    getTemplates(abort.signal).then(
      (answer) => {
        if (!abort.signal.aborted) {
          setTemplates({
            state: 'read',
            templates: new Map(answer.templates.map((template) => [template.id, template])),
          });
        }
      },
      (error: unknown) => {
        if (!abort.signal.aborted) {
          setTemplates({ state: 'failed', message: messageOf(error) });
        }
      },
    );
    return () => abort.abort();
  }, [changes]);
  return templates;
}

/**
 * The search (design 8.1 and 11): Ctrl+K focuses it from anywhere on the page, and its text goes into the address
 * `searchWait` after the last key, or at once with Enter, in place of the address it changes, so Back leaves the
 * search. Back and a link show the search of their address.
 */
function SearchField({ query }: { query: RecordsQuery }) {
  const location = useLocation();
  const navigate = useNavigate();
  const field = useRef<HTMLInputElement>(null);
  const [draft, setDraft] = useState<SearchDraft | null>(null);
  const addressText = query.q ?? '';
  const pending = pendingSearch(draft, location, addressText);

  useEffect(() => {
    if (pending === null) {
      return;
    }

    const timer = setTimeout(
      () => navigate(browsePath({ ...query, q: pending.text }), { replace: true, state: draftState(pending) }),
      searchWait,
    );
    return () => clearTimeout(timer);
  }, [pending, query, navigate]);

  useEffect(() => {
    const focus = (event: KeyboardEvent) => {
      if (isSearchShortcut(event)) {
        event.preventDefault();
        field.current?.focus();
        field.current?.select();
      }
    };
    document.addEventListener('keydown', focus);
    return () => document.removeEventListener('keydown', focus);
  }, []);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (pending !== null) {
      navigate(browsePath({ ...query, q: pending.text }), { replace: true, state: draftState(pending) });
    }
  };

  return (
    <form className="browse-search" role="search" onSubmit={submit}>
      <input
        ref={field}
        type="search"
        value={searchText(draft, location, addressText)}
        onChange={(event) => {
          const text = event.target.value;
          const id = newDraftId();
          setDraft((current) => typeSearch(current, location, text, id));
        }}
        aria-label={texts.browse.search}
        placeholder={texts.browse.searchHint}
        autoComplete="off"
        spellCheck={false}
      />
    </form>
  );
}

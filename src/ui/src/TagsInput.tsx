import { useEffect, useId, useState, type KeyboardEvent } from 'react';
import { ApiError, getTags } from './api.ts';
import { useChanges } from './changes.ts';
import { addTags, tagSuggestions } from './recordForm.ts';
import { useSessionStore } from './session.ts';
import { texts } from './texts.ts';

const t = texts.form;

/**
 * The tags of a record (M6.8): the chosen ones, each with its remove button, and an input that suggests the tags of the
 * records (`GET /api/tags`), filtered as the user types. Enter adds the suggestion chosen with the arrows, or the text
 * typed, a new tag too; a comma, typed or pasted, and leaving the input add the text typed; Backspace in the empty
 * input removes the last tag. `id` is on the input.
 */
export function TagsInput({ id, tags, onTags }: { id: string; tags: string[]; onTags: (tags: string[]) => void }) {
  const store = useSessionStore();
  const changes = useChanges();
  const listId = useId();
  const [known, setKnown] = useState<string[]>([]);
  const [typed, setTyped] = useState('');
  const [expanded, setExpanded] = useState(false);
  const [active, setActive] = useState(-1);

  useEffect(() => {
    const abort = new AbortController();
    // The suggestions only help, so a read that fails leaves none, and the form goes on.
    getTags(abort.signal).then(
      (all) => {
        if (!abort.signal.aborted) {
          setKnown(all);
        }
      },
      (error: unknown) => {
        if (!abort.signal.aborted && error instanceof ApiError && error.kind === 'refused' && error.status === 403) {
          store.reload();
        }
      },
    );
    return () => abort.abort();
  }, [changes, store]);

  const suggestions = tagSuggestions(known, tags, typed);
  const add = (text: string) => {
    onTags(addTags(tags, text));
    setTyped('');
    setActive(-1);
  };

  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        setExpanded(true);
        setActive((index) => Math.min(index + 1, suggestions.length - 1));
        break;
      case 'ArrowUp':
        event.preventDefault();
        setActive((index) => Math.max(index - 1, 0));
        break;
      case 'Enter': {
        const suggestion = expanded ? suggestions[active] : undefined;
        if (suggestion !== undefined || typed.trim() !== '') {
          event.preventDefault();
          add(suggestion ?? typed);
        }

        break;
      }
      case 'Backspace':
        if (typed === '' && tags.length > 0) {
          onTags(tags.slice(0, -1));
        }

        break;
      case 'Escape':
        if (expanded) {
          event.preventDefault();
          setExpanded(false);
          setActive(-1);
        }

        break;
    }
  };

  const activeSuggestion = expanded ? suggestions[active] : undefined;
  return (
    <div className="tags-input">
      {tags.length > 0 && (
        <ul className="tags-chosen">
          {tags.map((tag) => (
            <li key={tag} className="tag">
              <span>{tag}</span>
              <button type="button" aria-label={t.removeTag(tag)} onClick={() => onTags(tags.filter((x) => x !== tag))}>
                ×
              </button>
            </li>
          ))}
        </ul>
      )}
      <div className="form-combobox">
        <input
          id={id}
          role="combobox"
          aria-autocomplete="list"
          aria-expanded={expanded && suggestions.length > 0}
          aria-controls={listId}
          aria-activedescendant={activeSuggestion === undefined ? undefined : `${listId}-${String(active)}`}
          autoComplete="off"
          placeholder={t.tagsPlaceholder}
          value={typed}
          onChange={(event) => {
            // A comma, typed or pasted, adds the tags before it.
            const text = event.target.value;
            const comma = text.lastIndexOf(',');
            if (comma !== -1) {
              onTags(addTags(tags, text.slice(0, comma)));
            }

            setTyped(text.slice(comma + 1));
            setExpanded(true);
            setActive(-1);
          }}
          onFocus={() => setExpanded(true)}
          onBlur={() => {
            setExpanded(false);
            if (typed.trim() !== '') {
              add(typed);
            }
          }}
          onKeyDown={onKeyDown}
        />
        {expanded && suggestions.length > 0 && (
          <ul id={listId} role="listbox" className="form-options" aria-label={t.tags}>
            {suggestions.map((tag, index) => (
              <li
                key={tag}
                id={`${listId}-${String(index)}`}
                role="option"
                aria-selected={index === active}
                className="form-option"
                // The input keeps the focus, so its blur does not add the text typed before the click.
                onMouseDown={(event) => event.preventDefault()}
                onClick={() => add(tag)}
              >
                {tag}
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}

import { Link } from 'react-router';
import type { Facets, RecordsQuery, Template } from './api.ts';
import {
  browsePath,
  fieldFilters,
  hasFilters,
  shorten,
  tagChoices,
  templateChoices,
  withField,
  withoutFilters,
  withTag,
  withTemplate,
  type FilterChoice,
} from './browse.ts';
import { texts } from './texts.ts';

/**
 * The filters of the main view (M6.4, design 8.1), from the `facets` of `GET /api/records`, each choice with the
 * records that it gives: the type, the tags, of which a record has every one chosen, and each field with `filter` under
 * its label in the template. Each is a part of the query in the address, so a choice is a link, and Back takes it back.
 */
export function Filters({
  facets,
  query,
  templates,
}: {
  facets: Facets;
  query: RecordsQuery;
  templates: ReadonlyMap<string, Template> | undefined;
}) {
  const types = templateChoices(facets.templates, templates, query.template);
  const tags = tagChoices(facets.tags, query.tags ?? []);
  const fields = fieldFilters(facets.fields, templates, query.fields ?? {});
  return (
    <section className="filters" aria-label={texts.browse.filters}>
      <div className="filters-head">
        <h2>{texts.browse.filters}</h2>
        {hasFilters(query) && (
          <Link className="filters-clear" to={browsePath(withoutFilters(query))}>
            {texts.browse.clearAll}
          </Link>
        )}
      </div>
      <FilterGroup
        title={texts.browse.type}
        choices={types}
        to={(choice) => browsePath(withTemplate(query, choice.chosen ? undefined : choice.value))}
        clear={query.template === undefined ? undefined : browsePath(withTemplate(query, undefined))}
      />
      <FilterGroup
        title={texts.browse.tags}
        choices={tags}
        to={(choice) => browsePath(withTag(query, choice.value, !choice.chosen))}
        clear={(query.tags ?? []).length === 0 ? undefined : browsePath({ ...query, tags: undefined })}
      />
      {fields.map((field) => (
        <FilterGroup
          key={field.key}
          title={field.label}
          choices={field.choices}
          to={(choice) => browsePath(withField(query, field.key, choice.chosen ? undefined : choice.value))}
          clear={
            field.choices.some((choice) => choice.chosen)
              ? browsePath(withField(query, field.key, undefined))
              : undefined
          }
        />
      ))}
    </section>
  );
}

function FilterGroup({
  title,
  choices,
  to,
  clear,
}: {
  title: string;
  choices: FilterChoice[];
  to: (choice: FilterChoice) => string;
  clear: string | undefined;
}) {
  if (choices.length === 0) {
    return null;
  }

  return (
    <div className="filter-group">
      <div className="filter-head">
        <h3>{title}</h3>
        {clear !== undefined && (
          <Link className="filters-clear" to={clear} aria-label={texts.browse.clearFilter(title)}>
            {texts.browse.clear}
          </Link>
        )}
      </div>
      <ul className="filter-choices">
        {choices.map((choice) => (
          <li key={choice.value}>
            <Link
              className={choice.chosen ? 'filter-choice chosen' : 'filter-choice'}
              to={to(choice)}
              aria-current={choice.chosen ? 'true' : undefined}
              title={choice.label}
            >
              {shorten(choice.label, 40)} <span className="filter-count">({choice.count})</span>
            </Link>
          </li>
        ))}
      </ul>
    </div>
  );
}

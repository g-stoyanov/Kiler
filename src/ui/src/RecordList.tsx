import { Link } from 'react-router';
import type { ListItem, Template } from './api.ts';
import { pathText, shorten } from './browse.ts';
import { FieldValue } from './FieldValue.tsx';
import { recordPath } from './fields.ts';

/**
 * The records of the main view (docs/api.md, "List item"): the name as a link to the record, the template, the stash
 * path, the description, and the fields with `list` under their labels, each shortened. The template gives the names
 * and the types; without it, the id of the template and the keys show, and a `url` is text.
 */
export function RecordList({
  records,
  templates,
}: {
  records: ListItem[];
  templates: ReadonlyMap<string, Template> | undefined;
}) {
  return (
    <ul className="record-list">
      {records.map((record) => (
        <RecordRow key={record.id} record={record} template={templates?.get(record.template)} />
      ))}
    </ul>
  );
}

function RecordRow({ record, template }: { record: ListItem; template: Template | undefined }) {
  return (
    <li className="record-row">
      <div className="record-head">
        <Link className="record-name" to={recordPath(record.id)}>
          {record.name}
        </Link>
        <span className="mark">{template?.name ?? record.template}</span>
      </div>
      <div className="record-path">{pathText(record.stash)}</div>
      {record.description.kind === 'value' && (
        <div className="record-description">
          <FieldValue field={record.description} shorten={shorten} />
        </div>
      )}
      {record.fields.length > 0 && (
        <dl className="record-fields">
          {record.fields.map((field) => {
            const templateField = template?.fields.find((entry) => entry.key === field.key);
            return (
              <div key={field.key}>
                <dt>{templateField?.label ?? field.key}</dt>
                <dd>
                  <FieldValue field={field} type={templateField?.type} shorten={shorten} />
                </dd>
              </div>
            );
          })}
        </dl>
      )}
    </li>
  );
}

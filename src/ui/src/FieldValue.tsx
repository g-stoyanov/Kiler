import { Link } from 'react-router';
import type { FieldType, RecordField } from './api.ts';
import { CopyButton } from './CopyButton.tsx';
import { isCodeValue, isRefValue, recordPath, urlView, valueText } from './fields.ts';
import { texts } from './texts.ts';

/** The types whose text keeps its lines. */
const linesTypes: (FieldType | undefined)[] = ['multiline', 'markdown', 'doc'];

/**
 * A field of a record by its `kind` (docs/api.md, "Field"): the value, "No value", the padlock of a level that the
 * session does not hold, a field that cannot be read, and a `secret` as `••••`, whose value only its own request gives.
 * The padlock is its text alone; a screen puts its button beside it (decided at the start of M6).
 *
 * The value is told by its shape: a `ref` is a link to its record, or says that the record has gone, and a `code` is
 * code. `type`, of the field in the template, tells a `url` from a text, so without the template a `url` is text. A
 * `url` of this computer (`urlView` gives `local`) comes with its own "Copy" and the sentence why, so a screen that
 * puts "Copy" beside each value puts none beside it.
 * `shorten` gives the text that shows, such as a long value shortened in the list; a link and "Copy" keep the whole.
 */
export function FieldValue({
  field,
  type,
  shorten = (text) => text,
}: {
  field: RecordField;
  type?: FieldType;
  shorten?: (text: string) => string;
}) {
  switch (field.kind) {
    case 'empty':
      return <span className="field-value muted">{texts.field.empty}</span>;
    case 'secret':
      return <span className="field-value">{texts.field.secret}</span>;
    case 'locked':
      return <span className="field-value padlock">{texts.field.locked(field.levelName ?? String(field.level))}</span>;
    case 'unreadable':
      return (
        <span className="field-value field-unreadable" title={texts.field.unreadableHint}>
          {texts.field.unreadable}
        </span>
      );
    case 'value':
      break;
  }

  const value = field.value;
  if (isRefValue(value)) {
    return value.name === null ? (
      <span className="field-value muted">{texts.field.gone}</span>
    ) : (
      <Link className="field-value" to={recordPath(value.id)}>
        {shorten(value.name)}
      </Link>
    );
  }

  const text = valueText(value) ?? '';
  if (isCodeValue(value) || type === 'code') {
    return <code className="field-value field-code">{shorten(text)}</code>;
  }

  if (type === 'url') {
    const url = urlView(text);
    if (url.kind === 'link') {
      return (
        <a className="field-value" href={url.href} target="_blank" rel="noopener noreferrer">
          {shorten(text)}
        </a>
      );
    }

    if (url.kind === 'local') {
      return (
        <span className="field-value field-local">
          <span>{shorten(text)}</span>
          <CopyButton value={text} />
          <span className="muted">{texts.field.local}</span>
        </span>
      );
    }
  }

  return <span className={linesTypes.includes(type) ? 'field-value field-lines' : 'field-value'}>{shorten(text)}</span>;
}

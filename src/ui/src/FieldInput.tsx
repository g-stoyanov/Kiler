import type { ReactNode } from 'react';
import type { LevelName } from './api.ts';
import { levelLabel } from './admin.ts';
import { CodeEditor } from './CodeEditor.tsx';
import type { DraftValue, FieldView } from './recordForm.ts';
import { RefPicker } from './RefPicker.tsx';
import { texts } from './texts.ts';

const t = texts.form;

/**
 * A field of the record form (M6.7): its label, the input of its type, its level with the raise that the editor may
 * ask, and the problem under it. A field above the editor's levels shows only its padlock. `id` is on the element that
 * takes the focus, which its label names, so the form focuses it after a refusal of that field. `extra` goes under the
 * input, such as the `env` that the stash path suggests (M6.8).
 */
export function FieldInput({
  id,
  view,
  levels,
  problem,
  onValue,
  onLevel,
  extra,
}: {
  id: string;
  view: FieldView;
  levels: LevelName[];
  problem: string | undefined;
  onValue: (value: DraftValue) => void;
  onLevel: (level: number) => void;
  extra?: ReactNode;
}) {
  const { field, value } = view;
  const label = field.required ? t.required(field.label) : field.label;
  if (view.padlock !== null || value.kind === 'unreadable') {
    return (
      <div className="field form-field">
        <span className="form-label">{label}</span>
        {view.padlock !== null ? (
          <span className="padlock">{texts.field.locked(view.padlock)}</span>
        ) : (
          <span className="field-unreadable" title={texts.field.unreadableHint}>
            {texts.field.unreadable}
          </span>
        )}
        {problem !== undefined && <span className="field-problem">{problem}</span>}
      </div>
    );
  }

  return (
    <div className="field form-field">
      <div className="form-field-head">
        <label htmlFor={id}>{label}</label>
        <LevelChoice id={`${id}-level`} label={field.label} view={view} levels={levels} onLevel={onLevel} />
      </div>
      <Input id={id} view={view} onValue={onValue} />
      {extra}
      {problem !== undefined && <span className="field-problem">{problem}</span>}
    </div>
  );
}

/** The level of a field: a choice from its default up to the editor's top level, or its level alone without one. */
function LevelChoice({
  id,
  label,
  view,
  levels,
  onLevel,
}: {
  id: string;
  label: string;
  view: FieldView;
  levels: LevelName[];
  onLevel: (level: number) => void;
}) {
  if (view.choices.length <= 1) {
    return <span className="muted form-level">{t.levelText(levelLabel(view.level, levels))}</span>;
  }

  return (
    <select
      id={id}
      className="form-level"
      aria-label={t.level(label)}
      value={view.level}
      onChange={(event) => onLevel(Number(event.target.value))}
    >
      {view.choices.map((level) => (
        <option key={level} value={level}>
          {levelLabel(level, levels)}
        </option>
      ))}
    </select>
  );
}

/** The input of a field by its type; `markdown` and `doc` are a few lines of text until M7. */
function Input({ id, view, onValue }: { id: string; view: FieldView; onValue: (value: DraftValue) => void }) {
  const { field, value } = view;
  switch (value.kind) {
    case 'text':
      return <TextInput id={id} view={view} text={value.text} onText={(text) => onValue({ kind: 'text', text })} />;
    case 'code':
      return (
        <div className="stack form-code">
          {field.languages !== null && (
            <select
              aria-label={t.language(field.label)}
              value={value.language}
              onChange={(event) => onValue({ ...value, language: event.target.value })}
            >
              {field.languages.map((language) => (
                <option key={language} value={language}>
                  {language}
                </option>
              ))}
            </select>
          )}
          <CodeEditor
            id={id}
            value={value.code}
            onChange={(code) => onValue({ ...value, code })}
            language={field.languages === null ? field.language : value.language}
            dialect={field.dialect}
          />
        </div>
      );
    case 'ref':
      return <RefPicker id={id} field={field} value={value.ref} onRef={(ref) => onValue({ kind: 'ref', ref })} />;
    case 'secret':
      return (
        <div className="form-secret">
          <input
            id={id}
            type="password"
            autoComplete="new-password"
            value={value.typed}
            placeholder={value.kept && !value.clear ? t.secretKept : undefined}
            disabled={value.clear}
            onChange={(event) => onValue({ ...value, typed: event.target.value, again: false })}
          />
          {value.kept && (
            <label className="check">
              <input
                type="checkbox"
                checked={value.clear}
                onChange={(event) => onValue({ ...value, clear: event.target.checked, typed: '', again: false })}
              />
              {t.secretClear}
            </label>
          )}
        </div>
      );
    case 'locked':
    case 'unreadable':
      return null;
  }
}

function TextInput({
  id,
  view,
  text,
  onText,
}: {
  id: string;
  view: FieldView;
  text: string;
  onText: (text: string) => void;
}) {
  const { field } = view;
  switch (field.type) {
    case 'multiline':
    case 'markdown':
    case 'doc':
      return (
        <textarea
          id={id}
          rows={field.type === 'multiline' ? 3 : 8}
          value={text}
          onChange={(event) => onText(event.target.value)}
        />
      );
    case 'select': {
      // A value that the options no longer have, of a template changed since, stays among them, so it is not lost.
      const options = field.options ?? [];
      const all = text === '' || options.includes(text) ? options : [text, ...options];
      return (
        <select id={id} value={text} onChange={(event) => onText(event.target.value)}>
          <option value="">{t.noValue}</option>
          {all.map((option) => (
            <option key={option} value={option}>
              {option}
            </option>
          ))}
        </select>
      );
    }
    case 'number':
      return (
        <input
          id={id}
          type="number"
          step="any"
          className="narrow"
          value={text}
          onChange={(event) => onText(event.target.value)}
        />
      );
    case 'date':
      return <input id={id} type="date" value={text} onChange={(event) => onText(event.target.value)} />;
    case 'url':
      return <input id={id} type="url" value={text} onChange={(event) => onText(event.target.value)} />;
    default:
      return <input id={id} type="text" value={text} onChange={(event) => onText(event.target.value)} />;
  }
}

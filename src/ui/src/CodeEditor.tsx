/**
 * Code to edit, in the form: its value, its language and the dialect of a SQL, and `id` for its label (`Field.tsx`). A
 * `<textarea>` until M6.9 makes it CodeMirror with the same props.
 */
export function CodeEditor({
  id,
  value,
  onChange,
  language,
  dialect,
  disabled = false,
}: {
  id: string;
  value: string;
  onChange: (value: string) => void;
  language: string | null;
  dialect: string | null;
  disabled?: boolean;
}) {
  return (
    <textarea
      id={id}
      className="code-editor"
      value={value}
      onChange={(event) => onChange(event.target.value)}
      disabled={disabled}
      spellCheck={false}
      rows={8}
      data-language={language ?? undefined}
      data-dialect={dialect ?? undefined}
    />
  );
}

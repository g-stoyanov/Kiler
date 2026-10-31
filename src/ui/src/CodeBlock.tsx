/**
 * Code to read, in the record view: its language, and the dialect of a SQL. A `<pre>` until M6.9 makes it CodeMirror
 * with the same props.
 */
export function CodeBlock({
  code,
  language,
  dialect,
}: {
  code: string;
  language: string | null;
  dialect: string | null;
}) {
  return (
    <pre className="code-block" data-language={language ?? undefined} data-dialect={dialect ?? undefined}>
      <code>{code}</code>
    </pre>
  );
}

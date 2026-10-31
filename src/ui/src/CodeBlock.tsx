import { useRef, useState } from 'react';
import { CopyButton } from './CopyButton.tsx';
import { formatProblem, formatSql, isSql, useCodeView } from './code.ts';
import { texts } from './texts.ts';
import './Code.css';

/** "Format" of the code that it was made from, so the code of a new read shows as saved. */
type Formatted = { from: string } & ({ code: string } | { problem: string });

/**
 * Code to read, in the record view (design 8.3): CodeMirror with its language, and the dialect of a SQL, with "Copy" of
 * the code that shows and, for a SQL, "Format". The format only shows: the record keeps its code.
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
  const host = useRef<HTMLDivElement>(null);
  const [formatted, setFormatted] = useState<Formatted | null>(null);
  // Only a SQL formats, so a template whose language is no longer SQL shows the code as saved.
  const current = formatted?.from === code && isSql(language) ? formatted : null;
  const shown = current !== null && 'code' in current ? current.code : code;
  useCodeView(host, { code: shown, language, dialect, access: 'read' });

  const format = () => {
    try {
      setFormatted({ from: code, code: formatSql(code, dialect) });
    } catch (error) {
      setFormatted({ from: code, problem: formatProblem(error) });
    }
  };

  return (
    <div className="code">
      <div ref={host} className="code-view" data-language={language ?? undefined} />
      <div className="buttons">
        <CopyButton value={shown} />
        {isSql(language) &&
          (shown === code ? (
            <button type="button" onClick={format}>
              {texts.code.format}
            </button>
          ) : (
            <button type="button" onClick={() => setFormatted(null)}>
              {texts.code.asSaved}
            </button>
          ))}
      </div>
      {current !== null && 'problem' in current && (
        <p className="code-problem" role="alert">
          {texts.code.formatFailed(current.problem)}
        </p>
      )}
    </div>
  );
}

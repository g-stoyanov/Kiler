import { useEffect, useRef, useState } from 'react';
import { formatProblem, formatSql, isSql, useCodeView } from './code.ts';
import { texts } from './texts.ts';
import './Code.css';

/**
 * Code to edit, in the form (design 8.3): CodeMirror with its language, and the dialect of a SQL, with "Format" for a
 * SQL, which Ctrl+Z takes back. `id` is on the element that takes the focus and gives it to the editor, so
 * `getElementById(id).focus()` focuses it (M6.7, after a 400 with `field`).
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
  const host = useRef<HTMLDivElement>(null);
  // The sentence of a format that failed, for the code that it was tried on.
  const [problem, setProblem] = useState<{ from: string; text: string } | null>(null);
  const view = useCodeView(host, { code: value, language, dialect, access: disabled ? 'disabled' : 'edit', onChange });

  // The label of the field (`Field.tsx`) is for `id`, which is not an input: the editor sits in a shadow root, which
  // an id of the page does not reach. So the editor takes the text of the label as its name, and a click on the label
  // focuses it.
  useEffect(() => {
    const editor = view.current;
    const label = host.current?.ownerDocument.querySelector(`label[for="${CSS.escape(id)}"]`);
    if (editor === null || label === null || label === undefined) {
      return;
    }

    editor.contentDOM.setAttribute('aria-label', label.textContent ?? '');
    const focus = () => view.current?.focus();
    label.addEventListener('click', focus);
    return () => label.removeEventListener('click', focus);
  }, [id, view]);

  const format = () => {
    const editor = view.current;
    if (editor === null) {
      return;
    }

    // The code that the editor shows, which can be ahead of `value` while the user types.
    const code = editor.state.doc.toString();
    let formatted: string;
    try {
      formatted = formatSql(code, dialect);
    } catch (error) {
      setProblem({ from: code, text: formatProblem(error) });
      return;
    }

    // Through the editor, so the format is one step of its history; the editor gives the change to `onChange`.
    editor.dispatch({ changes: { from: 0, to: editor.state.doc.length, insert: formatted } });
    setProblem(null);
  };

  return (
    <div className="code">
      <div ref={host} id={id} tabIndex={-1} className="code-view" data-language={language ?? undefined} />
      {isSql(language) && (
        <div className="buttons">
          <button type="button" onClick={format} disabled={disabled}>
            {texts.code.format}
          </button>
        </div>
      )}
      {problem !== null && problem.from === value && isSql(language) && (
        <p className="code-problem" role="alert">
          {texts.code.formatFailed(problem.text)}
        </p>
      )}
    </div>
  );
}

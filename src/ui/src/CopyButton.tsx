import { useEffect, useRef, useState } from 'react';
import { messageOf } from './api.ts';
import { texts } from './texts.ts';

type CopyState = { state: 'none' | 'copying' | 'copied' } | { state: 'failed'; message: string };

/**
 * "Copy" of a value: the text itself, or a request that gives it only on the click, such as the value of a `secret`
 * (kiler-ui, "Secret values"). The request is aborted when the button goes, so nothing is copied after the page is
 * left, and its refusal shows by its sentence. The clipboard needs a secure context, which 127.0.0.1 is; when the
 * browser refuses, `copyFailed` says what to do instead.
 */
export function CopyButton({
  value,
  label,
  copyFailed = texts.copy.failed,
}: {
  value: string | ((signal: AbortSignal) => Promise<string>);
  /** The name of the button for a screen reader, where several are on one page, such as "Copy Host". */
  label?: string;
  copyFailed?: string;
}) {
  const [copy, setCopy] = useState<CopyState>({ state: 'none' });
  const abort = useRef<AbortController>(null);
  useEffect(() => () => abort.current?.abort(), []);

  // "Copied" goes after a moment, so the next copy of the same value shows too.
  useEffect(() => {
    if (copy.state !== 'copied') {
      return;
    }

    const timer = setTimeout(() => setCopy({ state: 'none' }), 2000);
    return () => clearTimeout(timer);
  }, [copy]);

  const write = (text: string) => {
    navigator.clipboard.writeText(text).then(
      () => setCopy({ state: 'copied' }),
      () => setCopy({ state: 'failed', message: copyFailed }),
    );
  };

  const copyValue = () => {
    if (navigator.clipboard === undefined) {
      setCopy({ state: 'failed', message: copyFailed });
      return;
    }

    if (typeof value === 'string') {
      write(value);
      return;
    }

    const request = new AbortController();
    abort.current = request;
    setCopy({ state: 'copying' });
    value(request.signal).then(
      (text) => {
        if (!request.signal.aborted) {
          write(text);
        }
      },
      (error: unknown) => {
        if (!request.signal.aborted) {
          setCopy({ state: 'failed', message: messageOf(error) });
        }
      },
    );
  };

  const caption =
    copy.state === 'copying' ? texts.copy.copying : copy.state === 'copied' ? texts.copy.copied : texts.copy.copy;
  return (
    <span className="copy-button">
      <button type="button" onClick={copyValue} disabled={copy.state === 'copying'} aria-label={label}>
        {caption}
      </button>
      {copy.state === 'failed' && (
        <span className="copy-failed" role="alert">
          {copy.message}
        </span>
      )}
    </span>
  );
}

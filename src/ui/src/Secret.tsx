import { useState } from 'react';
import { texts } from './texts.ts';

/**
 * A secret value that Kiler gives once, with a copy button (kiler-ui, "Secret values"): the recovery key of the wizard
 * and a temporary password. The screen that shows it keeps it only while it is open.
 */
export function Secret({ value, copyFailed }: { value: string; copyFailed: string }) {
  const [copy, setCopy] = useState<'none' | 'copied' | 'failed'>('none');
  const copyValue = () => {
    // The clipboard needs a secure context, which 127.0.0.1 is; without it, the value is copied by hand.
    if (navigator.clipboard === undefined) {
      setCopy('failed');
      return;
    }

    navigator.clipboard.writeText(value).then(
      () => setCopy('copied'),
      () => setCopy('failed'),
    );
  };

  return (
    <>
      <div className="secret">
        <code className="secret-value">{value}</code>
        <button type="button" onClick={copyValue}>
          {copy === 'copied' ? texts.secret.copied : texts.secret.copy}
        </button>
      </div>
      {copy === 'failed' && (
        <p className="banner banner-danger" role="alert">
          {copyFailed}
        </p>
      )}
    </>
  );
}

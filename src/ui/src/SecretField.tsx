import { useSyncExternalStore } from 'react';
import { requestSecret, type RecordField } from './api.ts';
import { CopyButton } from './CopyButton.tsx';
import { FieldValue } from './FieldValue.tsx';
import { readsSession, shownSeconds, ShownSecrets } from './recordView.ts';
import { useSessionStore } from './session.ts';
import { texts } from './texts.ts';

/**
 * A `secret` with a value (design 8.2): `••••`, with "Show" and "Copy" for a signed-in session, each one request of the
 * value that says its `use` (kiler-ui, "Secret values"). A shown value is in `secrets`, which hides it again; a copied
 * one goes only to the clipboard, and its request is aborted when the button goes. A guest gets no buttons, as the
 * request refuses a guest.
 */
export function SecretField({
  recordId,
  field,
  label,
  buttons,
  secrets,
}: {
  recordId: string;
  field: RecordField;
  label: string;
  buttons: boolean;
  secrets: ShownSecrets;
}) {
  const store = useSessionStore();
  const view = ShownSecrets.viewOf(useSyncExternalStore(secrets.subscribe, secrets.getSnapshot), field.key);
  if (!buttons) {
    return <FieldValue field={field} />;
  }

  const copy = (signal: AbortSignal) =>
    requestSecret(recordId, field.key, 'copy', signal).catch((error: unknown) => {
      if (!signal.aborted && readsSession(error)) {
        store.reload();
      }

      throw error;
    });

  return (
    <span className="secret-field">
      {view.state === 'shown' ? (
        <span className="field-value secret-shown">{view.value}</span>
      ) : (
        <FieldValue field={field} />
      )}
      {view.state === 'shown' ? (
        <button type="button" onClick={() => secrets.hide(field.key)} aria-label={texts.record.hideLabel(label)}>
          {texts.record.hide}
        </button>
      ) : (
        <button
          type="button"
          onClick={() => secrets.show(field.key)}
          disabled={view.state === 'showing'}
          aria-label={texts.record.showLabel(label)}
        >
          {view.state === 'showing' ? texts.record.showing : texts.record.show}
        </button>
      )}
      <CopyButton value={copy} label={texts.record.copyLabel(label)} copyFailed={texts.record.copyFailed} />
      {view.state === 'shown' && <span className="muted secret-hint">{texts.record.hidesAfter(shownSeconds)}</span>}
      {view.state === 'failed' && (
        <span className="copy-failed" role="alert">
          {view.message}
        </span>
      )}
    </span>
  );
}

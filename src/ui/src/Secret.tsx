import { CopyButton } from './CopyButton.tsx';

/**
 * A secret value that Kiler gives once, with a copy button (kiler-ui, "Secret values"): the recovery key of the wizard
 * and a temporary password. The screen that shows it keeps it only while it is open.
 */
export function Secret({ value, copyFailed }: { value: string; copyFailed: string }) {
  return (
    <div className="secret">
      <code className="secret-value">{value}</code>
      <CopyButton value={value} copyFailed={copyFailed} />
    </div>
  );
}

import type { ReactNode } from 'react';

/** A field of a form: its label, the input, and the problem of the page's checks under it. */
export function Field({
  id,
  label,
  problem,
  children,
}: {
  id: string;
  label: string;
  problem: string | undefined;
  children: ReactNode;
}) {
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      {children}
      {problem !== undefined && <span className="field-problem">{problem}</span>}
    </div>
  );
}

import type { ReactNode } from 'react';
import type { Session } from './api.ts';
import { texts } from './texts.ts';

/**
 * A record at `/records/<id>` (M6.5): its header, its fields and their padlocks, and "Show" and "Copy" of a `secret`.
 * `App.tsx` mounts it again for another `id`. Its title until then, with the props that `App.tsx` gives it.
 */
export const RecordPage: (props: { id: string; session: Session }) => ReactNode = () => (
  <section className="card stack">
    <h1>{texts.record.title}</h1>
  </section>
);

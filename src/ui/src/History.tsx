import type { ReactNode } from 'react';
import type { Session } from './api.ts';
import { texts } from './texts.ts';

/**
 * The versions of a record at `/records/<id>/history` (M6.14), with "Restore" of an old one. `App.tsx` mounts it again
 * for another `id`. Its title until then, with the props that `App.tsx` gives it.
 */
export const History: (props: { id: string; session: Session }) => ReactNode = () => (
  <section className="card stack">
    <h1>{texts.history.title}</h1>
  </section>
);

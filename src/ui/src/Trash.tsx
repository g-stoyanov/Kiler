import type { ReactNode } from 'react';
import type { Session } from './api.ts';
import { texts } from './texts.ts';

/** The trash at `/trash` (M6.14), with "Restore". Its title until then, with the props that `App.tsx` gives it. */
export const Trash: (props: { session: Session }) => ReactNode = () => (
  <section className="card stack">
    <h1>{texts.history.trashTitle}</h1>
  </section>
);

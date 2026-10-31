import type { ReactNode } from 'react';
import type { Session } from './api.ts';
import { texts } from './texts.ts';

/**
 * The main view at `/` (M6.3, M6.4): the search, the list, the stash tree and the filters, with the query in the address.
 * Its title until then, with the props that `App.tsx` gives it.
 */
export const Browse: (props: { session: Session }) => ReactNode = () => (
  <section className="card stack">
    <h1>{texts.browse.title}</h1>
  </section>
);

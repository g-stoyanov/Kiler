import type { Session } from './api.ts';
import { texts } from './texts.ts';

/**
 * The form of a record (M6.6, M6.7, M6.8): a new one at `/new`, with the path of `?stash=` from a "+", and the edit of
 * the record `id` at `/records/<id>/edit`. Only a session with `canEdit` reaches it (`App.tsx`), which mounts it again
 * for another `id`. Its title until then, with the props that `App.tsx` gives it.
 */
export function RecordForm({ id }: { id?: string; session: Session }) {
  return (
    <section className="card stack">
      <h1>{id === undefined ? texts.form.newTitle : texts.form.editTitle}</h1>
    </section>
  );
}

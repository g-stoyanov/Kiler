import { useId, type CSSProperties } from 'react';
import type { VersionCell, VersionRow } from './recordForm.ts';
import { texts } from './texts.ts';

const t = texts.form.versions;

/**
 * The two versions of a conflict side by side (M6.20): "Your edit" and the version of `current`, headed `theirs` with
 * who saved it and when, each a column with every row of `conflictRows` (`recordForm.ts`), and a row where the two
 * differ marked in both. The rows of the two columns line up, and in a narrow window the columns stack
 * (`RecordForm.css`). Each column is a region named by its heading. The values are text only, with no link, so nothing
 * in the panel leaves the form and its edit.
 */
export function ConflictVersions({ rows, theirs }: { rows: VersionRow[]; theirs: string }) {
  // The grid has a row for the heading and one for each row of the versions, which the columns share.
  const style = { '--conflict-rows': String(rows.length) } as CSSProperties;
  return (
    <div className="conflict-versions" style={style}>
      <Version heading={t.mine} rows={rows} side="mine" />
      <Version heading={theirs} rows={rows} side="theirs" />
    </div>
  );
}

function Version({ heading, rows, side }: { heading: string; rows: VersionRow[]; side: 'mine' | 'theirs' }) {
  const id = useId();
  return (
    <section className="conflict-version" aria-labelledby={id}>
      <h2 id={id}>{heading}</h2>
      <dl>
        {rows.map((row) => (
          <div key={row.key} className={row.differs ? 'conflict-row conflict-differs' : 'conflict-row'}>
            <dt>
              {row.label}
              {row.differs && (
                <>
                  {' '}
                  <span className="conflict-mark">{t.differs}</span>
                </>
              )}
            </dt>
            <dd>
              <Cell cell={row[side]} />
            </dd>
          </div>
        ))}
      </dl>
    </section>
  );
}

function Cell({ cell }: { cell: VersionCell }) {
  switch (cell.look) {
    case 'value':
      return cell.text;
    case 'lines':
      return <span className="conflict-lines">{cell.text}</span>;
    case 'code':
      return (
        <>
          {cell.language !== null && <span className="muted">{cell.language}</span>}
          <pre className="conflict-code field-code">{cell.text}</pre>
        </>
      );
    case 'note':
      return <span className="muted">{cell.text}</span>;
    case 'padlock':
      return <span className="padlock">{cell.text}</span>;
  }
}

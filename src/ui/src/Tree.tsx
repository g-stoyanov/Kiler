import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import type { RecordsQuery, StashNode } from './api.ts';
import {
  browsePath,
  filterKey,
  isNodeOpen,
  newRecordPath,
  nodeKey,
  nodePlace,
  readOpenNodes,
  revealNode,
  saveOpenNodes,
  stashName,
  toggleNode,
  treeState,
  treeTop,
  type NodePlace,
  type TreeState,
} from './browse.ts';
import { texts } from './texts.ts';

/** What every node of the tree reads. */
interface TreeView {
  tree: TreeState;
  query: RecordsQuery;
  filter: string;
  chosen: string;
  canEdit: boolean;
  toggle: (key: string) => void;
}

/**
 * The stash tree of the main view (design 6.6), from the `tree` of `GET /api/records`, with the records at each node
 * and under it. A node chosen is the `stash` of the query; "All records" clears it. It starts collapsed to level 1,
 * with the nodes that the user opened in this run of Kiler (browse.ts), and opens the chosen node and those above it.
 * During a search or a filter Kiler gives only the branches with results, and they show open. "+" opens the form at the
 * node, and the one above the tree without a path, for an editor only (decided at the start of M6).
 */
export function Tree({ nodes, query, canEdit }: { nodes: StashNode[]; query: RecordsQuery; canEdit: boolean }) {
  const path = query.stash ?? [];
  const chosen = nodeKey(path);
  const filter = filterKey(query);
  const [tree, setTree] = useState(() => revealNode(treeState(readOpenNodes()), path));
  // A node chosen by another way than the tree, such as Back, shows too.
  const [revealed, setRevealed] = useState(chosen);
  if (revealed !== chosen) {
    setRevealed(chosen);
    setTree((current) => revealNode(current, path));
  }

  useEffect(() => {
    saveOpenNodes(tree.open);
  }, [tree.open]);

  const view: TreeView = {
    tree,
    query,
    filter,
    chosen,
    canEdit,
    toggle: (key) => setTree((current) => toggleNode(current, key, filter)),
  };
  const total = nodes.reduce((sum, node) => sum + node.count, 0);
  return (
    <nav className="tree" aria-label={texts.browse.stashes}>
      <div className="tree-head">
        <h2>{texts.browse.stashes}</h2>
        {canEdit && (
          <Link
            className="button tree-new"
            to={newRecordPath()}
            aria-label={texts.browse.newRecord}
            title={texts.browse.newRecord}
          >
            +
          </Link>
        )}
      </div>
      <div className={chosen === '' ? 'tree-row chosen' : 'tree-row'}>
        <Link
          className="tree-name"
          to={browsePath({ ...query, stash: undefined })}
          aria-current={chosen === '' ? 'true' : undefined}
        >
          {texts.browse.all} <span className="tree-count">({total})</span>
        </Link>
      </div>
      <Nodes nodes={nodes} parent={treeTop} view={view} />
    </nav>
  );
}

function Nodes({ nodes, parent, view }: { nodes: StashNode[]; parent: NodePlace; view: TreeView }) {
  return (
    <ul className="tree-nodes">
      {nodes.map((node) => {
        const place = nodePlace(parent, node);
        const name = stashName(node);
        const newRecord = texts.browse.newRecordIn(place.names.join(texts.browse.pathSeparator));
        const open = node.children.length > 0 && isNodeOpen(view.tree, place.key, view.filter);
        const chosen = place.key === view.chosen;
        return (
          <li key={node.id}>
            <div className={chosen ? 'tree-row chosen' : 'tree-row'}>
              {node.children.length > 0 ? (
                <button
                  type="button"
                  className="tree-toggle"
                  aria-expanded={open}
                  aria-label={open ? texts.browse.closeNode(name) : texts.browse.openNode(name)}
                  onClick={() => view.toggle(place.key)}
                >
                  {open ? '▾' : '▸'}
                </button>
              ) : (
                <span className="tree-toggle" />
              )}
              <Link
                className={node.name === null ? 'tree-name muted' : 'tree-name'}
                to={browsePath({ ...view.query, stash: place.path })}
                aria-current={chosen ? 'true' : undefined}
              >
                {name} <span className="tree-count">({node.count})</span>
              </Link>
              {view.canEdit && (
                <Link
                  className="button tree-new"
                  to={newRecordPath(place.path)}
                  aria-label={newRecord}
                  title={newRecord}
                >
                  +
                </Link>
              )}
            </div>
            {open && <Nodes nodes={node.children} parent={place} view={view} />}
          </li>
        );
      })}
    </ul>
  );
}

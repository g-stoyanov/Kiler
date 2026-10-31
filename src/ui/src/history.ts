import type { PathStash, RecordVersion, Session } from './api.ts';
import { texts } from './texts.ts';

/*
 * Who gets which button of the delete, the trash and the history (M6.14, docs/api.md, "Decided in M5.1"): an editor who
 * holds the level of every group of the record, and for an old version, of the current version and of the old one. A
 * restore writes the groups of every level, so without it an editor of a lower level could bring back an old Prod
 * password. An admin holds every level. The server checks every request anyway.
 */

/** Whether the session holds every level of `levels`, the levels of the groups of a record or a version. */
export function holdsEvery(session: Session, levels: number[]): boolean {
  return levels.every((level) => session.levels.includes(level));
}

/** Whether the session may write over a record with the groups `levels`: an editor who holds every one of them. */
function mayWriteOver(session: Session, levels: number[]): boolean {
  return session.signedIn && session.canEdit && holdsEvery(session, levels);
}

/** "Delete" in the record view, by the levels of the record. */
export function offersDelete(session: Session, record: { levels: number[] }): boolean {
  return mayWriteOver(session, record.levels);
}

/** "Restore" of a trash item, by its levels. */
export function offersTrashRestore(session: Session, item: { levels: number[] }): boolean {
  return mayWriteOver(session, item.levels);
}

/**
 * "Restore" of a version of the history, whose first version is the current one: an old version only, for an editor
 * who holds the levels of the current version and of the old one.
 */
export function offersVersionRestore(session: Session, versions: RecordVersion[], version: RecordVersion): boolean {
  const current = versions[0];
  return (
    current !== undefined &&
    version.version !== current.version &&
    mayWriteOver(session, current.levels) &&
    holdsEvery(session, version.levels)
  );
}

/** The stash path of a trash item as one text, as its record is not there to link to. */
export function pathText(stash: PathStash[]): string {
  return stash.map((part) => part.name ?? texts.history.unknownStash).join(' › ');
}

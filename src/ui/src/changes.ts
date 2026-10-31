/**
 * The changes of other users (decided at the start of M6): a number that grows at each `changed` event of the stream,
 * so a screen that reads records puts it among the dependencies of its read, and reads again at each. M6.11 sends the
 * event and makes the number grow; until then it is 0, and no screen changes with it.
 */
export function useChanges(): number {
  return 0;
}

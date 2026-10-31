import { useSyncExternalStore } from 'react';

/**
 * The input of the user in the page, after which the reads are the user's again: a key, a pointer (a click, a touch, a
 * pen), a click that a screen reader or the keyboard makes without the others, a text typed another way, and Back or
 * Forward of the browser.
 */
export const inputEvents = ['keydown', 'pointerdown', 'click', 'input', 'popstate'] as const;

/**
 * The changes of other users (M6.11, step 9 of M6): a number that grows at each `changed` event of the stream, so a
 * screen that reads records puts it among the dependencies of its read (decided at the start of M6) and reads again at
 * each. Kiler sends the event after a load of the shared folder that changed the data, and after each write of this
 * Kiler, which no load gives, so the other pages of this Kiler see it too.
 *
 * Such a read is no activity for the automatic lock (decided by the user in M6.11), or a page that the changes of others
 * keep reading would never lock: from a `changed` event until the user's next input in the page ({@link inputEvents}),
 * the reads follow the change ({@link follows}), and `api.ts` marks each `GET` then, which Kiler does not count. A read
 * that comes after a click or a key counts as before.
 */
export class Changes {
  #count = 0;
  #follows = false;
  readonly #listeners = new Set<() => void>();

  /** The number of `changed` events of this page. */
  get count(): number {
    return this.#count;
  }

  /** Whether a `changed` event came after the user's last input, so a read now is the page's own. */
  get follows(): boolean {
    return this.#follows;
  }

  /** A `changed` event: the number grows, and the reads follow the change until the user's next input. */
  note(): void {
    this.#count += 1;
    this.#follows = true;
    for (const listener of this.#listeners) {
      listener();
    }
  }

  /** An input of the user: the reads after it are the user's again. */
  input(): void {
    this.#follows = false;
  }

  /** Calls the listener at each `changed` event. Returns the function that stops the calls. */
  subscribe(listener: () => void): () => void {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }

  /**
   * Takes each {@link inputEvents} of `target`, the window, as an input of the user, in the capture phase, so a handler
   * that stops the event does not hide it. Returns the function that stops.
   */
  watchInput(target: EventTarget): () => void {
    const onInput = () => this.input();
    for (const type of inputEvents) {
      target.addEventListener(type, onInput, { capture: true });
    }

    return () => {
      for (const type of inputEvents) {
        target.removeEventListener(type, onInput, { capture: true });
      }
    };
  }
}

/** The changes of this page, which `App.tsx` feeds from the stream and the input, and `api.ts` asks at each request. */
export const changes = new Changes();

const subscribe = (listener: () => void) => changes.subscribe(listener);
const count = () => changes.count;

/** The number of `changed` events of this page, which a screen that reads records puts among the dependencies of its read. */
export function useChanges(): number {
  return useSyncExternalStore(subscribe, count);
}

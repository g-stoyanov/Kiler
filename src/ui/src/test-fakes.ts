// Fakes of the browser's EventSource and MessagePort for the tests of events.ts and connection.ts.
import type { HubMessage, PageMessage, PagePort } from './events.ts';

/** An EventSource that the test drives. The browser's reconnect does not happen once `close` is called. */
export class FakeEventSource {
  static created: FakeEventSource[] = [];
  readonly url: string;
  closed = false;
  readonly #listeners = new Map<string, ((event: Event) => void)[]>();

  constructor(url: string) {
    this.url = url;
    FakeEventSource.created.push(this);
  }

  addEventListener(type: string, listener: (event: Event) => void): void {
    this.#listeners.set(type, [...(this.#listeners.get(type) ?? []), listener]);
  }

  close(): void {
    this.closed = true;
  }

  emit(type: string): void {
    for (const listener of this.#listeners.get(type) ?? []) {
      listener(new Event(type));
    }
  }
}

export class FakePort implements PagePort {
  readonly received: HubMessage[] = [];
  started = false;
  closed = false;
  readonly sent: PageMessage[] = [];
  #listener: ((event: MessageEvent) => void) | undefined;

  postMessage(message: HubMessage): void {
    this.received.push(message);
  }

  addEventListener(_type: 'message', listener: (event: MessageEvent) => void): void {
    this.#listener = listener;
  }

  start(): void {
    this.started = true;
  }

  close(): void {
    this.closed = true;
  }

  /** A message from the other side of the port. */
  deliver(data: unknown): void {
    this.#listener?.(new MessageEvent('message', { data }));
  }
}

export function lastSource(): FakeEventSource {
  const source = FakeEventSource.created.at(-1);
  if (source === undefined) {
    throw new Error('No EventSource was created.');
  }

  return source;
}

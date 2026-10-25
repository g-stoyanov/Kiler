import { KilerEvents, type HubMessage, type PageMessage, type StateListener } from './events.ts';

/**
 * The page side of the heartbeat (events.ts): joins the stream that the SharedWorker shares between the pages, or
 * opens one of its own without SharedWorker or when the worker does not start. Returns the function that leaves.
 */
export function connectEvents(onState: StateListener): () => void {
  if (typeof SharedWorker === 'function') {
    let left = false;
    let fellBack = false;
    let leave: () => void = () => {};
    try {
      // Vite finds the worker by this exact form: new URL('<literal>', import.meta.url) inside new SharedWorker.
      const worker = new SharedWorker(new URL('./events-worker.ts', import.meta.url), {
        type: 'module',
        name: 'kiler-events',
      });
      worker.port.addEventListener('message', (event: MessageEvent<HubMessage>) => {
        if (event.data.type === 'state') {
          onState(event.data.state);
        }
      });
      worker.port.start();
      leave = () => {
        worker.port.postMessage({ type: 'bye' } satisfies PageMessage);
        worker.port.close();
      };
      worker.addEventListener('error', () => {
        // Once only: a second fallback would open a new stream after a stop.
        if (left || fellBack) {
          return;
        }

        // The worker did not start, so this page holds a stream of its own.
        fellBack = true;
        leave();
        leave = connectOwn(onState);
      });
      return () => {
        left = true;
        leave();
      };
    } catch {
      // The browser refused the worker: the same as no SharedWorker.
    }
  }

  return connectOwn(onState);
}

function connectOwn(onState: StateListener): () => void {
  const events = new KilerEvents();
  const unsubscribe = events.subscribe(onState);
  events.start();
  return () => {
    unsubscribe();
    events.close();
  };
}

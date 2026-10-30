import { KilerEvents, type HubMessage, type PageMessage, type SessionListener, type StateListener } from './events.ts';

/**
 * The page side of the heartbeat (events.ts): joins the stream that the SharedWorker shares between the pages, or
 * opens one of its own without SharedWorker or when the worker does not start. Behind the worker, the first state comes
 * once the port is in the worker's set, so no `session` event after it is missed. Returns the function that leaves.
 */
export function connectEvents(onState: StateListener, onSession: SessionListener): () => void {
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
        } else if (event.data.type === 'session') {
          onSession();
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
        leave = connectOwn(onState, onSession);
      });
      return () => {
        left = true;
        leave();
      };
    } catch {
      // The browser refused the worker: the same as no SharedWorker.
    }
  }

  return connectOwn(onState, onSession);
}

function connectOwn(onState: StateListener, onSession: SessionListener): () => void {
  const events = new KilerEvents();
  const unsubscribe = events.subscribe(onState);
  const unsubscribeSession = events.onSession(onSession);
  events.start();
  return () => {
    unsubscribe();
    unsubscribeSession();
    events.close();
  };
}

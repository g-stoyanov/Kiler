// The SharedWorker that holds the one event stream of all the Kiler pages (events.ts).
import { createEventsHub, KilerEvents } from './events.ts';

declare const self: SharedWorkerGlobalScope;

const hub = createEventsHub(new KilerEvents());

self.addEventListener('connect', (event) => {
  const port = event.ports[0];
  if (port !== undefined) {
    hub.connect(port);
  }
});

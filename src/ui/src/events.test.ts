import { beforeEach, describe, expect, it } from 'vitest';
import { createEventsHub, eventsPath, KilerEvents, type ConnectionState, type PageMessage } from './events.ts';
import { FakeEventSource, FakePort, lastSource } from './test-fakes.ts';

function newEvents(): KilerEvents {
  return new KilerEvents((url) => new FakeEventSource(url) as unknown as EventSource);
}

beforeEach(() => {
  FakeEventSource.created = [];
});

describe('KilerEvents', () => {
  it('opens one stream on /api/events and is connecting until it opens', () => {
    const events = newEvents();

    events.start();

    expect(FakeEventSource.created.map((source) => source.url)).toEqual([eventsPath]);
    expect(events.state).toBe('connecting');
  });

  it.each(['open', 'ping'])('is connected after "%s"', (type) => {
    const events = newEvents();
    events.start();

    lastSource().emit(type);

    expect(events.state).toBe('connected');
  });

  it('closes the stream on the first error, in the handler, so the browser does not reconnect', () => {
    const events = newEvents();
    events.start();
    lastSource().emit('open');

    lastSource().emit('error');

    expect(lastSource().closed).toBe(true);
    expect(events.state).toBe('stopped');
  });

  it('opens no new stream by itself after a stop', () => {
    const events = newEvents();
    const states: ConnectionState[] = [];
    events.subscribe((state) => states.push(state));
    events.start();
    lastSource().emit('open');

    lastSource().emit('error');
    lastSource().emit('error');
    lastSource().emit('open');

    expect(FakeEventSource.created).toHaveLength(1);
    expect(events.state).toBe('stopped');
    expect(states).toEqual(['connecting', 'connected', 'stopped']);
  });

  it('opens one stream for any number of starts', () => {
    const events = newEvents();

    events.start();
    events.start();

    expect(FakeEventSource.created).toHaveLength(1);
  });

  it('opens a new stream when a new page starts it after a stop', () => {
    const events = newEvents();
    events.start();
    const first = lastSource();
    first.emit('error');

    events.start();
    first.emit('open');

    expect(FakeEventSource.created).toHaveLength(2);
    expect(events.state).toBe('connecting');
    lastSource().emit('ping');
    expect(events.state).toBe('connected');
  });

  it('closes the stream for a page that goes away, without calling it a stop', () => {
    const events = newEvents();
    events.start();

    events.close();

    expect(lastSource().closed).toBe(true);
    expect(events.state).toBe('connecting');
  });

  it('tells a listener the state now and each change, until it unsubscribes', () => {
    const events = newEvents();
    const states: ConnectionState[] = [];

    const unsubscribe = events.subscribe((state) => states.push(state));
    events.start();
    lastSource().emit('open');
    lastSource().emit('ping');
    unsubscribe();
    lastSource().emit('error');

    expect(states).toEqual(['connecting', 'connected']);
  });
});

describe('createEventsHub', () => {
  it('starts the stream for the first page and sends it the state', () => {
    const events = newEvents();
    const hub = createEventsHub(events);
    const page = new FakePort();

    hub.connect(page);

    expect(page.started).toBe(true);
    expect(FakeEventSource.created).toHaveLength(1);
    expect(page.received).toEqual([{ type: 'state', state: 'connecting' }]);
  });

  it('shares one stream between the pages and sends each of them every change', () => {
    const events = newEvents();
    const hub = createEventsHub(events);
    const first = new FakePort();
    const second = new FakePort();

    hub.connect(first);
    lastSource().emit('open');
    hub.connect(second);
    lastSource().emit('error');

    expect(FakeEventSource.created).toHaveLength(1);
    expect(first.received.map((message) => message.state)).toEqual(['connecting', 'connected', 'stopped']);
    expect(second.received.map((message) => message.state)).toEqual(['connected', 'stopped']);
  });

  it('sends nothing more to a page that said bye', () => {
    const hub = createEventsHub(newEvents());
    const page = new FakePort();
    hub.connect(page);

    page.deliver({ type: 'bye' } satisfies PageMessage);
    lastSource().emit('open');

    expect(page.received).toEqual([{ type: 'state', state: 'connecting' }]);
  });

  it('opens a new stream for a page that loads after a stop, and tells the open pages', () => {
    const hub = createEventsHub(newEvents());
    const old = new FakePort();
    hub.connect(old);
    lastSource().emit('error');

    const fresh = new FakePort();
    hub.connect(fresh);

    expect(FakeEventSource.created).toHaveLength(2);
    expect(old.received.map((message) => message.state)).toEqual(['connecting', 'stopped', 'connecting']);
    expect(fresh.received).toEqual([{ type: 'state', state: 'connecting' }]);
  });
});

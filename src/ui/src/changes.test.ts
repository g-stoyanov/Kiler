// @vitest-environment jsdom
import { describe, expect, it } from 'vitest';
import { Changes, inputEvents } from './changes.ts';

describe('Changes', () => {
  it('grows at each "changed" event and tells each listener, until it unsubscribes', () => {
    const changes = new Changes();
    const counts: number[] = [];
    const unsubscribe = changes.subscribe(() => counts.push(changes.count));

    changes.note();
    changes.note();
    unsubscribe();
    changes.note();

    expect(changes.count).toBe(3);
    expect(counts).toEqual([1, 2]);
  });

  it('makes the reads follow a change from the event until the next input of the user', () => {
    const changes = new Changes();
    expect(changes.follows).toBe(false);

    changes.note();
    expect(changes.follows).toBe(true);

    changes.input();
    expect(changes.follows).toBe(false);

    changes.note();
    changes.note();
    expect(changes.follows).toBe(true);
  });

  it.each(inputEvents)('takes "%s" of the window as an input, until it stops watching', (type) => {
    const changes = new Changes();
    const stop = changes.watchInput(window);

    changes.note();
    window.dispatchEvent(new Event(type));
    expect(changes.follows).toBe(false);

    stop();
    changes.note();
    window.dispatchEvent(new Event(type));
    expect(changes.follows).toBe(true);
  });

  it('takes an input that a handler of the page stops, as it watches in the capture phase', () => {
    const changes = new Changes();
    const stop = changes.watchInput(window);
    const button = document.body.appendChild(document.createElement('button'));
    button.addEventListener('click', (event) => event.stopPropagation());

    changes.note();
    button.dispatchEvent(new MouseEvent('click', { bubbles: true }));

    expect(changes.follows).toBe(false);
    stop();
    button.remove();
  });

  it('does not take other events as an input, such as a scroll or the focus', () => {
    const changes = new Changes();
    const stop = changes.watchInput(window);

    changes.note();
    window.dispatchEvent(new Event('scroll'));
    window.dispatchEvent(new Event('focus'));

    expect(changes.follows).toBe(true);
    stop();
  });
});

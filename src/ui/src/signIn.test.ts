import { describe, expect, it } from 'vitest';
import { newSignInState, returnPath, signInFrom, signInReducer, type SignInState } from './signIn.ts';

function typed(password: string): SignInState {
  return signInReducer(newSignInState, { type: 'edit', password });
}

describe('signInReducer', () => {
  it('lets the password go once its request is sent', () => {
    const sending = signInReducer(typed('demo-password'), { type: 'send' });

    expect(sending).toEqual({ password: '', sending: true, error: undefined });
  });

  it("shows Kiler's sentence of a refusal, and asks for the password again", () => {
    const sending = signInReducer(typed('demo-password'), { type: 'send' });

    const refused = signInReducer(sending, { type: 'refused', message: 'The password is wrong.' });

    expect(refused).toEqual({ password: '', sending: false, error: 'The password is wrong.' });
  });

  it('keeps the message of the last refusal while the password is typed again, and drops it with the next request', () => {
    const refused = signInReducer(signInReducer(typed('demo-password'), { type: 'send' }), {
      type: 'refused',
      message: 'No access. Contact an admin.',
    });

    const again = signInReducer(refused, { type: 'edit', password: 'other-password' });
    expect(again.error).toBe('No access. Contact an admin.');
    expect(signInReducer(again, { type: 'send' }).error).toBeUndefined();
  });

  it('takes no typing and no second request while a request runs', () => {
    const sending = signInReducer(typed('demo-password'), { type: 'send' });

    expect(signInReducer(sending, { type: 'edit', password: 'typed-meanwhile' })).toBe(sending);
    expect(signInReducer(sending, { type: 'send' })).toBe(sending);
  });

  it('takes no refusal without a request', () => {
    const state = typed('demo-password');

    expect(signInReducer(state, { type: 'refused', message: 'The password is wrong.' })).toBe(state);
  });
});

describe('returnPath', () => {
  it('goes back to the address of signInFrom, with its query', () => {
    expect(returnPath(signInFrom({ pathname: '/records/1', search: '?tab=history' }))).toBe('/records/1?tab=history');
  });

  it.each([
    ['/password', '/password'],
    ['/records/1?tab=history', '/records/1?tab=history'],
    ['/', '/'],
  ])('goes back to %s', (from, path) => {
    expect(returnPath({ from })).toBe(path);
  });

  it.each([
    ['no state', undefined],
    ['a state without from', { other: '/password' }],
    ['a from that is not text', { from: 1 }],
    ['a relative path', { from: 'password' }],
    ['another host', { from: '//example.com/' }],
    ['another host with a backslash', { from: '/\\example.com/' }],
    ['a full address', { from: 'https://example.com/' }],
    ['the sign-in', { from: '/sign-in' }],
    ['the sign-in with a query', { from: '/sign-in?x=1' }],
  ])('goes to the start page for %s', (_, state) => {
    expect(returnPath(state)).toBe('/');
  });
});

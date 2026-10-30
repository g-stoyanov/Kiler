/**
 * The sign-in form (M4.4, M4.8), also the lock screen: it keeps the password only until its request is sent, and a
 * refusal shows Kiler's sentence and asks for the password again (kiler-ui, "Secret values").
 */
export interface SignInState {
  password: string;
  sending: boolean;
  error: string | undefined;
}

export type SignInAction = { type: 'edit'; password: string } | { type: 'send' } | { type: 'refused'; message: string };

export const newSignInState: SignInState = { password: '', sending: false, error: undefined };

export function signInReducer(state: SignInState, action: SignInAction): SignInState {
  switch (action.type) {
    case 'edit':
      return state.sending ? state : { ...state, password: action.password };
    case 'send':
      // The request has the password by now; the form lets it go.
      return state.sending ? state : { password: '', sending: true, error: undefined };
    case 'refused':
      return state.sending ? { ...state, sending: false, error: action.message } : state;
  }
}

/** What the routes put into the state of the address when they send a page without a session to the sign-in. */
export interface SignInFrom {
  from: string;
}

/** The state of the address for the sign-in, so it goes back to `location` once it has a session. */
export function signInFrom(location: { pathname: string; search: string }): SignInFrom {
  return { from: location.pathname + location.search };
}

/**
 * Where the sign-in goes once it has a session: back to the address that it replaced, as after a lock, or else `/`.
 * Only a path of this origin (`//` and `/\` would name another host), never the sign-in itself.
 */
export function returnPath(state: unknown): string {
  const from = typeof state === 'object' && state !== null && 'from' in state ? state.from : undefined;
  return typeof from === 'string' && /^\/(?![/\\])/.test(from) && !isSignIn(from) ? from : '/';
}

function isSignIn(path: string): boolean {
  return path === '/sign-in' || path.startsWith('/sign-in?') || path.startsWith('/sign-in/');
}

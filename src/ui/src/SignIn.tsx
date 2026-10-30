import { useEffect, useId, useReducer, useRef, type FormEvent } from 'react';
import { Navigate, useLocation } from 'react-router';
import { messageOf, signIn, type Session } from './api.ts';
import { useSessionStore } from './session.ts';
import { newSignInState, returnPath, signInReducer } from './signIn.ts';
import { texts } from './texts.ts';

/**
 * The sign-in with the password of this Kiler's account (M4.4, M4.8), and the lock screen once this page has seen its
 * session end (`locked`): unlocking is a sign-in. A refusal shows Kiler's sentence: the wrong password apart from
 * "No access", which sends the user to an admin. With a session it goes back to the address that it replaced.
 */
export function SignIn({ session, locked }: { session: Session; locked: boolean }) {
  const store = useSessionStore();
  const location = useLocation();
  const passwordId = useId();
  const passwordField = useRef<HTMLInputElement>(null);
  const [state, dispatch] = useReducer(signInReducer, newSignInState);

  // The field is disabled while its request runs, so it has lost the focus by the refusal.
  useEffect(() => {
    if (!state.sending && state.error !== undefined) {
      passwordField.current?.focus();
    }
  }, [state.sending, state.error]);

  if (session.signedIn) {
    return <Navigate to={returnPath(location.state)} replace />;
  }

  const title = locked ? texts.signIn.lockedTitle : texts.signIn.title;
  if (session.account === null) {
    return (
      <section className="card stack">
        <h1>{title}</h1>
        <p className="banner banner-danger" role="alert">
          {texts.signIn.noAccount}
        </p>
      </section>
    );
  }

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (state.sending) {
      return;
    }

    const typed = state.password;
    dispatch({ type: 'send' });
    signIn(typed).then(
      (signedIn) => store.set(signedIn),
      (failure: unknown) => dispatch({ type: 'refused', message: messageOf(failure) }),
    );
  };

  return (
    <section className="card">
      <h1>{title}</h1>
      <form className="stack" onSubmit={submit}>
        {locked && <p>{texts.signIn.locked}</p>}
        <p className="muted">{texts.signIn.account(session.account)}</p>
        <div className="field">
          <label htmlFor={passwordId}>{texts.signIn.password}</label>
          <input
            ref={passwordField}
            id={passwordId}
            type="password"
            autoComplete="current-password"
            value={state.password}
            onChange={(event) => dispatch({ type: 'edit', password: event.target.value })}
            disabled={state.sending}
            autoFocus
          />
        </div>
        {state.error !== undefined && (
          <p className="banner banner-danger" role="alert">
            {state.error}
          </p>
        )}
        <div>
          <button type="submit" className="primary" disabled={state.sending || state.password === ''}>
            {submitText(locked, state.sending)}
          </button>
        </div>
      </form>
    </section>
  );
}

function submitText(locked: boolean, sending: boolean): string {
  if (locked) {
    return sending ? texts.signIn.unlocking : texts.signIn.unlock;
  }

  return sending ? texts.signIn.signingIn : texts.signIn.submit;
}

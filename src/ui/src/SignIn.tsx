import { useId, useState, type FormEvent } from 'react';
import { Navigate } from 'react-router';
import { messageOf, signIn, type Session } from './api.ts';
import { useSessionStore } from './session.ts';
import { texts } from './texts.ts';

/**
 * The sign-in with the password of this Kiler's account (M4.4). The page keeps the password only until its request is
 * sent. Its messages, the change of password and the lock screen are M4.8.
 */
export function SignIn({ session }: { session: Session }) {
  const store = useSessionStore();
  const passwordId = useId();
  const [password, setPassword] = useState('');
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string>();
  if (session.signedIn) {
    return <Navigate to="/" replace />;
  }

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (sending) {
      return;
    }

    const typed = password;
    setPassword('');
    setSending(true);
    setError(undefined);
    signIn(typed).then(
      (signedIn) => store.set(signedIn),
      (failure: unknown) => {
        setSending(false);
        setError(messageOf(failure));
      },
    );
  };

  return (
    <section className="card">
      <h1>{texts.signIn.title}</h1>
      <form className="stack" onSubmit={submit}>
        <p className="muted">
          {session.account === null ? texts.signIn.noAccount : texts.signIn.account(session.account)}
        </p>
        <div className="field">
          <label htmlFor={passwordId}>{texts.signIn.password}</label>
          <input
            id={passwordId}
            type="password"
            autoComplete="current-password"
            value={password}
            onChange={(event) => setPassword(event.target.value)}
            disabled={sending}
            autoFocus
          />
        </div>
        {error !== undefined && (
          <p className="banner banner-danger" role="alert">
            {error}
          </p>
        )}
        <div>
          <button type="submit" className="primary" disabled={sending || password === ''}>
            {sending ? texts.signIn.signingIn : texts.signIn.submit}
          </button>
        </div>
      </form>
    </section>
  );
}

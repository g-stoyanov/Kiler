import { useEffect, useId, useReducer, useRef, useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router';
import { changePassword, lockSession, messageOf, type Session } from './api.ts';
import {
  afterPasswordRefusal,
  checkPasswordForm,
  hasPasswordProblems,
  newPasswordState,
  passwordReducer,
  passwordRequest,
  type PasswordProblems,
} from './changePassword.ts';
import { Field } from './Field.tsx';
import { useSessionStore } from './session.ts';
import { texts } from './texts.ts';

/**
 * The change of password (M4.5a, M4.8): the forced one after a temporary password, which the routes show before
 * anything else, and the voluntary one with the current password. The forced change goes on to the start page; the
 * voluntary one says that the password has been changed. When Kiler asks for a new sign-in, the page offers it.
 */
export function ChangePassword({ session }: { session: Session }) {
  const store = useSessionStore();
  const navigate = useNavigate();
  const id = useId();
  const firstField = useRef<HTMLInputElement>(null);
  const forced = session.mustChangePassword;
  const [state, dispatch] = useReducer(passwordReducer, newPasswordState);
  // The problems show once the user has tried to send, and follow the fields from then on.
  const [checked, setChecked] = useState(false);
  const [lock, setLock] = useState<'none' | 'locking' | 'failed'>('none');

  // The fields are disabled while the request runs, so they have lost the focus by the refusal.
  useEffect(() => {
    if (!state.sending && state.error !== undefined) {
      firstField.current?.focus();
    }
  }, [state.sending, state.error]);

  const problems: PasswordProblems = checked ? checkPasswordForm(state.form, forced) : {};
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (state.sending) {
      return;
    }

    if (hasPasswordProblems(checkPasswordForm(state.form, forced))) {
      setChecked(true);
      return;
    }

    const request = passwordRequest(state.form, forced);
    setChecked(false);
    dispatch({ type: 'send' });
    changePassword(request).then(
      (changed) => {
        dispatch({ type: 'changed' });
        store.set(changed);
        if (forced) {
          void navigate('/', { replace: true });
        }
      },
      (error: unknown) => {
        const after = afterPasswordRefusal(error, forced);
        dispatch({ type: 'refused', message: messageOf(error), signInAgain: after === 'signInAgain' });
        if (after === 'readSession') {
          store.reload();
        }
      },
    );
  };

  // A new sign-in is a lock first: the sign-in shows only without a session, and the routes then send this address
  // to it, to come back here. A failed lock does not read the session again, which would take this screen and its
  // message away while the read runs.
  const signInAgain = () => {
    setLock('locking');
    lockSession().then(
      (locked) => store.set(locked),
      () => setLock('failed'),
    );
  };

  const edit = (field: 'current' | 'password' | 'repeat', value: string) =>
    dispatch({ type: 'edit', form: { ...state.form, [field]: value } });

  return (
    <section className="card">
      <h1>{forced ? texts.password.forcedTitle : texts.password.title}</h1>
      <form className="stack" onSubmit={submit} noValidate>
        {forced && <p>{texts.password.forcedIntro}</p>}
        <fieldset className="stack" disabled={state.sending}>
          {!forced && (
            <Field id={`${id}-current`} label={texts.password.current} problem={problems.current}>
              <input
                ref={firstField}
                id={`${id}-current`}
                type="password"
                autoComplete="current-password"
                value={state.form.current}
                onChange={(event) => edit('current', event.target.value)}
                autoFocus
              />
            </Field>
          )}
          <Field id={`${id}-password`} label={texts.password.password} problem={problems.password}>
            <input
              ref={forced ? firstField : undefined}
              id={`${id}-password`}
              type="password"
              autoComplete="new-password"
              value={state.form.password}
              onChange={(event) => edit('password', event.target.value)}
              autoFocus={forced}
            />
          </Field>
          <Field id={`${id}-repeat`} label={texts.password.repeat} problem={problems.repeat}>
            <input
              id={`${id}-repeat`}
              type="password"
              autoComplete="new-password"
              value={state.form.repeat}
              onChange={(event) => edit('repeat', event.target.value)}
            />
          </Field>
        </fieldset>
        {state.error !== undefined && (
          <p className="banner banner-danger" role="alert">
            {state.error}
          </p>
        )}
        {lock === 'failed' && (
          <p className="banner banner-danger" role="alert">
            {texts.account.lockFailed}
          </p>
        )}
        {state.changed && (
          <p className="banner banner-ok" role="status">
            {texts.password.changed}
          </p>
        )}
        <div className="buttons">
          <button type="submit" className="primary" disabled={state.sending || lock === 'locking'}>
            {state.sending ? texts.password.sending : texts.password.submit}
          </button>
          {state.signInAgain && (
            <button type="button" onClick={signInAgain} disabled={state.sending || lock === 'locking'}>
              {texts.password.signInAgain}
            </button>
          )}
        </div>
      </form>
    </section>
  );
}

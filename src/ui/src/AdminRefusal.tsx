import { useState } from 'react';
import { lockSession } from './api.ts';
import { useSessionStore } from './session.ts';
import { texts } from './texts.ts';

/**
 * Kiler's sentence of a refused admin request, with "Sign in again" when `levels.json` has other checks than at the
 * sign-in. A new sign-in is a lock first, as in the change of password: the routes then send the page to the sign-in,
 * which comes back to this address (kiler-ui, "Layout"). A failed lock says so and reads nothing again.
 */
export function AdminRefusal({ message, signInAgain }: { message: string; signInAgain: boolean }) {
  const store = useSessionStore();
  const [lock, setLock] = useState<'none' | 'locking' | 'failed'>('none');
  const lockAndSignIn = () => {
    setLock('locking');
    lockSession().then(
      (locked) => store.set(locked),
      () => setLock('failed'),
    );
  };

  return (
    <>
      <p className="banner banner-danger" role="alert">
        {message}
      </p>
      {lock === 'failed' && (
        <p className="banner banner-danger" role="alert">
          {texts.account.lockFailed}
        </p>
      )}
      {signInAgain && (
        <div>
          <button type="button" onClick={lockAndSignIn} disabled={lock === 'locking'}>
            {texts.admin.signInAgain}
          </button>
        </div>
      )}
    </>
  );
}

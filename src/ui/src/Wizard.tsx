import { useId, useReducer, useState, type FormEvent, type ReactNode } from 'react';
import { ApiError, messageOf, runWizard } from './api.ts';
import { texts } from './texts.ts';
import {
  checkWizard,
  hasProblems,
  limits,
  newWizardState,
  wizardReducer,
  wizardRequest,
  type WizardForm,
  type WizardProblems,
} from './wizard.ts';

/**
 * The wizard of a new Kiler (M4.3, design 7.7): the levels, the first admin and the settings, then the recovery key
 * once, until the user says it is saved. A 409 means the wizard cannot run, or made no Kiler, so the state of the data
 * is read again (`onRefused`); `onDone` comes once the key has left the state.
 */
export function Wizard({ onRefused, onDone }: { onRefused: () => void; onDone: () => void }) {
  const [state, dispatch] = useReducer(wizardReducer, undefined, newWizardState);
  // The problems show once the user has tried to send, and follow the fields from then on.
  const [checked, setChecked] = useState(false);

  switch (state.step) {
    case 'done':
      return null;
    case 'key':
      return (
        <RecoveryKey
          recoveryKey={state.recoveryKey}
          saved={state.saved}
          onSaved={(saved) => dispatch({ type: 'saved', saved })}
          onContinue={() => {
            dispatch({ type: 'confirm' });
            onDone();
          }}
        />
      );
  }

  const problems = checked ? checkWizard(state.form) : {};
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (state.sending) {
      return;
    }

    if (hasProblems(checkWizard(state.form))) {
      setChecked(true);
      return;
    }

    const request = wizardRequest(state.form);
    setChecked(false);
    dispatch({ type: 'send' });
    runWizard(request).then(
      (recoveryKey) => dispatch({ type: 'made', recoveryKey }),
      (error: unknown) => {
        dispatch({ type: 'refused', message: messageOf(error) });
        if (error instanceof ApiError && error.status === 409) {
          onRefused();
        }
      },
    );
  };

  return (
    <WizardFormView
      form={state.form}
      problems={problems}
      sending={state.sending}
      error={state.error}
      onEdit={(form) => dispatch({ type: 'edit', form })}
      onSubmit={submit}
    />
  );
}

function WizardFormView({
  form,
  problems,
  sending,
  error,
  onEdit,
  onSubmit,
}: {
  form: WizardForm;
  problems: WizardProblems;
  sending: boolean;
  error: string | undefined;
  onEdit: (form: WizardForm) => void;
  onSubmit: (event: FormEvent) => void;
}) {
  const id = useId();
  const top = form.levels.length - 1;
  const setLevel = (level: number, name: string) =>
    onEdit({ ...form, levels: form.levels.map((old, index) => (index === level ? name : old)) });

  return (
    <section className="card">
      <h1>{texts.wizard.title}</h1>
      <form className="stack" onSubmit={onSubmit} noValidate>
        <p>{texts.wizard.intro}</p>
        <fieldset className="stack" disabled={sending}>
          <legend>{texts.wizard.levels}</legend>
          <p className="muted">{texts.wizard.levelsHint}</p>
          {problems.levels !== undefined && <Problem text={problems.levels} />}
          {form.levels.map((name, level) => (
            <Field
              key={level}
              id={`${id}-level-${level}`}
              label={texts.wizard.levelName(level)}
              problem={problems.levelNames?.[level]}
            >
              <input
                id={`${id}-level-${level}`}
                value={name}
                onChange={(event) => setLevel(level, event.target.value)}
              />
            </Field>
          ))}
          <div className="buttons">
            <button
              type="button"
              disabled={form.levels.length >= limits.maxLevels}
              onClick={() => onEdit({ ...form, levels: [...form.levels, ''] })}
            >
              {texts.wizard.addLevel}
            </button>
            <button
              type="button"
              disabled={form.levels.length <= limits.minLevels}
              onClick={() => onEdit({ ...form, levels: form.levels.slice(0, top) })}
            >
              {texts.wizard.removeLevel}
            </button>
          </div>
        </fieldset>
        <fieldset className="stack" disabled={sending}>
          <legend>{texts.wizard.admin}</legend>
          <Field id={`${id}-name`} label={texts.wizard.displayName} problem={problems.displayName}>
            <input
              id={`${id}-name`}
              autoComplete="name"
              value={form.displayName}
              onChange={(event) => onEdit({ ...form, displayName: event.target.value })}
            />
          </Field>
          <Field id={`${id}-password`} label={texts.wizard.password} problem={problems.password}>
            <input
              id={`${id}-password`}
              type="password"
              autoComplete="new-password"
              value={form.password}
              onChange={(event) => onEdit({ ...form, password: event.target.value })}
            />
          </Field>
          <Field id={`${id}-repeat`} label={texts.wizard.repeat} problem={problems.repeat}>
            <input
              id={`${id}-repeat`}
              type="password"
              autoComplete="new-password"
              value={form.repeat}
              onChange={(event) => onEdit({ ...form, repeat: event.target.value })}
            />
          </Field>
        </fieldset>
        <fieldset className="stack" disabled={sending}>
          <legend>{texts.wizard.settings}</legend>
          <label className="check">
            <input
              type="checkbox"
              checked={form.guestMode}
              onChange={(event) => onEdit({ ...form, guestMode: event.target.checked })}
            />
            {texts.wizard.guestMode}
          </label>
          <Field id={`${id}-minutes`} label={texts.wizard.autoLockMinutes} problem={problems.autoLockMinutes}>
            <input
              id={`${id}-minutes`}
              className="narrow"
              type="number"
              inputMode="numeric"
              min={1}
              max={limits.maxAutoLockMinutes}
              step={1}
              value={form.autoLockMinutes}
              onChange={(event) => onEdit({ ...form, autoLockMinutes: event.target.value })}
            />
          </Field>
        </fieldset>
        {error !== undefined && <Problem text={error} />}
        <div>
          <button type="submit" className="primary" disabled={sending}>
            {sending ? texts.wizard.making : texts.wizard.make}
          </button>
        </div>
      </form>
    </section>
  );
}

/**
 * The recovery key, shown once (design 7.7). It stays in the state only while this screen is open: "Continue" takes
 * it away, and only once the user has said it is saved.
 */
function RecoveryKey({
  recoveryKey,
  saved,
  onSaved,
  onContinue,
}: {
  recoveryKey: string;
  saved: boolean;
  onSaved: (saved: boolean) => void;
  onContinue: () => void;
}) {
  const [copy, setCopy] = useState<'none' | 'copied' | 'failed'>('none');
  const copyKey = () => {
    // The clipboard needs a secure context, which 127.0.0.1 is; without it, the key is copied by hand.
    if (navigator.clipboard === undefined) {
      setCopy('failed');
      return;
    }

    navigator.clipboard.writeText(recoveryKey).then(
      () => setCopy('copied'),
      () => setCopy('failed'),
    );
  };

  return (
    <section className="card stack">
      <h1>{texts.wizard.keyTitle}</h1>
      <p>{texts.wizard.keyIntro}</p>
      <p>{texts.wizard.keyKeep}</p>
      <div className="secret">
        <code className="recovery-key">{recoveryKey}</code>
        <button type="button" onClick={copyKey}>
          {copy === 'copied' ? texts.wizard.copied : texts.wizard.copy}
        </button>
      </div>
      {copy === 'failed' && <Problem text={texts.wizard.copyFailed} />}
      <label className="check">
        <input type="checkbox" checked={saved} onChange={(event) => onSaved(event.target.checked)} />
        {texts.wizard.saved}
      </label>
      <div>
        <button type="button" className="primary" disabled={!saved} onClick={onContinue}>
          {texts.wizard.continue}
        </button>
      </div>
    </section>
  );
}

function Field({
  id,
  label,
  problem,
  children,
}: {
  id: string;
  label: string;
  problem: string | undefined;
  children: ReactNode;
}) {
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      {children}
      {problem !== undefined && <span className="field-problem">{problem}</span>}
    </div>
  );
}

function Problem({ text }: { text: string }) {
  return (
    <p className="banner banner-danger" role="alert">
      {text}
    </p>
  );
}

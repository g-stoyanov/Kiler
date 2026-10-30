import { useEffect, useId, useReducer, useState, type FormEvent } from 'react';
import {
  addUser,
  changeLevel,
  getAdminLevels,
  getAdminUsers,
  messageOf,
  setRights,
  type AdminLevel,
  type AdminUser,
  type AdminUserAnswer,
  type NewUserRequest,
  type RightsRequest,
  type Session,
} from './api.ts';
import {
  afterAdminRefusal,
  checkNewUser,
  hasAdminProblems,
  keysGiven,
  levelChoices,
  levelLabel,
  newUserForm,
  newUsersState,
  newUserRequest,
  usersReducer,
  type NewUserProblems,
  type TemporaryPassword,
} from './admin.ts';
import { AdminRefusal } from './AdminRefusal.tsx';
import { Field } from './Field.tsx';
import { Secret } from './Secret.tsx';
import { useSessionStore } from './session.ts';
import { texts } from './texts.ts';

/**
 * The users of the team (M4.9, design 7.6 and 8.4): the list with the marks and the warning of too few admins, a new
 * user, and on another user a new temporary password with the level that the admin gives, and the editor and admin
 * rights. An action that gives keys shows its temporary password once, in place of the list, until it is closed. The
 * admin's own row has no actions: another admin changes it, and the password is changed on the password screen.
 */
export function Users({ session }: { session: Session }) {
  const store = useSessionStore();
  const [state, dispatch] = useReducer(usersReducer, newUsersState);
  // A new value reads the list again; the last list shows meanwhile.
  const [round, setRound] = useState(0);
  const readAgain = () => {
    dispatch({ type: 'reading' });
    setRound((value) => value + 1);
  };

  useEffect(() => {
    const abort = new AbortController();
    Promise.all([getAdminUsers(abort.signal), getAdminLevels(abort.signal)]).then(
      ([users, levels]) => {
        if (!abort.signal.aborted) {
          dispatch({ type: 'read', users, levels });
        }
      },
      // A refusal of the read only says why: reading the session again would show this screen again, and its read
      // again, while the session still says that it is an admin's.
      (error: unknown) => {
        if (!abort.signal.aborted) {
          const signInAgain = afterAdminRefusal(error) === 'signInAgain';
          dispatch({ type: 'readFailed', message: messageOf(error), signInAgain });
        }
      },
    );
    return () => abort.abort();
  }, [round]);

  const act = (request: () => Promise<AdminUserAnswer>) => {
    if (state.sending) {
      return;
    }

    dispatch({ type: 'send' });
    request().then(
      (answer) => {
        dispatch({ type: 'done', answer });
        readAgain();
      },
      (error: unknown) => {
        const after = afterAdminRefusal(error);
        dispatch({ type: 'refused', message: messageOf(error), signInAgain: after === 'signInAgain' });
        if (after === 'readSession') {
          store.reload();
        } else if (after === 'readList') {
          readAgain();
        }
      },
    );
  };

  const list = state.list;
  if (state.temporary !== undefined) {
    return (
      <TemporaryPasswordView
        temporary={state.temporary}
        levels={list.state === 'ready' ? list.levels : []}
        onClose={() => dispatch({ type: 'closePassword' })}
      />
    );
  }

  if (list.state === 'reading') {
    return <p className="muted">{texts.admin.reading}</p>;
  }

  if (list.state === 'failed') {
    return (
      <section className="card stack">
        <h1>{texts.users.title}</h1>
        <AdminRefusal message={list.message} signInAgain={list.signInAgain} />
        <div>
          <button type="button" onClick={readAgain}>
            {texts.admin.tryAgain}
          </button>
        </div>
      </section>
    );
  }

  const { users, levels } = list;
  const panel = state.panel;
  const close = () => dispatch({ type: 'open', panel: { kind: 'none' } });
  // A user that a new list no longer has, or whose file no longer fits, has no panel.
  const chosen =
    panel.kind === 'user'
      ? users.users.find((user) => user.account === panel.account && user.invalid === null && user.level !== null)
      : undefined;

  return (
    <section className="card card-wide stack">
      <h1>{texts.users.title}</h1>
      {users.fewAdmins && (
        <p className="banner banner-warning" role="status">
          {texts.users.fewAdmins}
        </p>
      )}
      {state.done !== undefined && (
        <p className="banner banner-ok" role="status">
          {state.done}
        </p>
      )}
      <UserTable
        users={users.users}
        levels={levels}
        own={session.account}
        chosen={chosen?.account}
        disabled={state.sending}
        onChoose={(account) => dispatch({ type: 'open', panel: { kind: 'user', account } })}
      />
      {panel.kind !== 'new' && chosen === undefined && (
        <div>
          <button type="button" onClick={() => dispatch({ type: 'open', panel: { kind: 'new' } })}>
            {texts.users.add}
          </button>
        </div>
      )}
      {panel.kind === 'new' && (
        <NewUserPanel
          levels={levels}
          sending={state.sending}
          onSend={(request) => act(() => addUser(request))}
          onClose={close}
        />
      )}
      {chosen !== undefined && chosen.level !== null && (
        <UserPanel
          key={chosen.account}
          user={chosen}
          level={chosen.level}
          levels={levels}
          sending={state.sending}
          onLevel={(level) => act(() => changeLevel(chosen.account, level))}
          onRights={(rights) => act(() => setRights(chosen.account, rights))}
          onClose={close}
        />
      )}
      {state.error !== undefined && <AdminRefusal message={state.error} signInAgain={state.signInAgain} />}
    </section>
  );
}

function UserTable({
  users,
  levels,
  own,
  chosen,
  disabled,
  onChoose,
}: {
  users: AdminUser[];
  levels: AdminLevel[];
  own: string | null;
  chosen: string | undefined;
  disabled: boolean;
  onChoose: (account: string) => void;
}) {
  const t = texts.users;
  if (users.length === 0) {
    return <p className="muted">{t.none}</p>;
  }

  return (
    <div className="table-frame">
      <table className="table">
        <thead>
          <tr>
            <th scope="col">{t.account}</th>
            <th scope="col">{t.displayName}</th>
            <th scope="col">{t.level}</th>
            <th scope="col">{t.rights}</th>
            <th scope="col">{t.marks}</th>
            <th scope="col">
              <span className="visually-hidden">{t.change}</span>
            </th>
          </tr>
        </thead>
        <tbody>
          {users.map((user) => {
            const isOwn = user.account === own;
            return (
              <tr key={user.account} className={user.account === chosen ? 'chosen' : undefined}>
                <td>{user.account}</td>
                <td>{user.displayName}</td>
                <td>{user.level === null ? '' : levelLabel(user.level, levels)}</td>
                <td>{rightsOf(user)}</td>
                <td>
                  <Marks user={user} own={isOwn} />
                </td>
                <td>
                  {!isOwn && user.invalid === null && (
                    <button type="button" disabled={disabled} onClick={() => onChoose(user.account)}>
                      {t.change}
                    </button>
                  )}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function rightsOf(user: AdminUser): string {
  const rights: string[] = [];
  if (user.canEdit) {
    rights.push(texts.users.editor);
  }

  if (user.isAdmin) {
    rights.push(texts.users.admin);
  }

  return rights.join(', ');
}

/** The marks of a user file: the admin's own, a temporary password, keys that do not match the level, and a damaged file. */
function Marks({ user, own }: { user: AdminUser; own: boolean }) {
  const t = texts.users;
  return (
    <span className="marks">
      {own && <span className="mark">{t.you}</span>}
      {user.invalid !== null && <span className="mark mark-danger">{t.unreadable(user.invalid)}</span>}
      {user.invalid === null && !user.keysMatchLevel && (
        <span className="mark mark-danger" title={t.keysMismatchHint}>
          {t.keysMismatch}
        </span>
      )}
      {user.mustChangePassword && (
        <span className="mark" title={t.temporaryHint}>
          {t.temporary}
        </span>
      )}
    </span>
  );
}

/** The level picker of 1 to the top level, and which keys the chosen level gives. */
function LevelField({
  id,
  levels,
  choices,
  level,
  problem,
  onChange,
}: {
  id: string;
  levels: AdminLevel[];
  choices: AdminLevel[];
  level: number;
  problem: string | undefined;
  onChange: (level: number) => void;
}) {
  const keys = keysGiven(level, levels)
    .map((entry) => levelLabel(entry.level, levels))
    .join(', ');
  return (
    <Field id={id} label={texts.users.levelField} problem={problem}>
      <select id={id} value={level} onChange={(event) => onChange(Number(event.target.value))}>
        {choices.map((entry) => (
          <option key={entry.level} value={entry.level}>
            {levelLabel(entry.level, levels)}
          </option>
        ))}
      </select>
      {keys !== '' && <span className="muted">{texts.users.keys(keys)}</span>}
    </Field>
  );
}

function NewUserPanel({
  levels,
  sending,
  onSend,
  onClose,
}: {
  levels: AdminLevel[];
  sending: boolean;
  onSend: (request: NewUserRequest) => void;
  onClose: () => void;
}) {
  const id = useId();
  const t = texts.users;
  const [form, setForm] = useState(newUserForm);
  // The problems show once the admin has tried to send, and follow the fields from then on.
  const [checked, setChecked] = useState(false);
  const problems: NewUserProblems = checked ? checkNewUser(form, levels) : {};
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (hasAdminProblems(checkNewUser(form, levels))) {
      setChecked(true);
      return;
    }

    setChecked(false);
    onSend(newUserRequest(form));
  };

  return (
    <form className="panel stack" onSubmit={submit} noValidate>
      <h2>{t.newTitle}</h2>
      <fieldset className="stack" disabled={sending}>
        <Field id={`${id}-account`} label={t.accountField} problem={problems.account}>
          <input
            id={`${id}-account`}
            autoComplete="off"
            spellCheck={false}
            value={form.account}
            onChange={(event) => setForm({ ...form, account: event.target.value })}
            autoFocus
          />
          <span className="muted">{t.accountHint}</span>
        </Field>
        <Field id={`${id}-name`} label={t.nameField} problem={problems.displayName}>
          <input
            id={`${id}-name`}
            autoComplete="off"
            value={form.displayName}
            onChange={(event) => setForm({ ...form, displayName: event.target.value })}
          />
        </Field>
        <LevelField
          id={`${id}-level`}
          levels={levels}
          choices={levelChoices(levels, false)}
          level={form.level}
          problem={problems.level}
          onChange={(level) => setForm({ ...form, level })}
        />
        <label className="check">
          <input
            type="checkbox"
            checked={form.canEdit}
            onChange={(event) => setForm({ ...form, canEdit: event.target.checked })}
          />
          {t.editorField}
        </label>
      </fieldset>
      <div className="buttons">
        <button type="submit" className="primary" disabled={sending}>
          {sending ? t.creating : t.create}
        </button>
        <button type="button" onClick={onClose} disabled={sending}>
          {t.close}
        </button>
      </div>
    </form>
  );
}

/**
 * The actions on another user: a new temporary password with a level that the admin gives, the level of the file
 * chosen first (M4.6), and the rights, sent whole. An admin keeps the top level.
 */
function UserPanel({
  user,
  level,
  levels,
  sending,
  onLevel,
  onRights,
  onClose,
}: {
  user: AdminUser;
  level: number;
  levels: AdminLevel[];
  sending: boolean;
  onLevel: (level: number) => void;
  onRights: (rights: RightsRequest) => void;
  onClose: () => void;
}) {
  const id = useId();
  const t = texts.users;
  const choices = levelChoices(levels, user.isAdmin);
  const [chosenLevel, setChosenLevel] = useState(() =>
    choices.some((entry) => entry.level === level) ? level : (choices[0]?.level ?? level),
  );
  const [rights, setRightsForm] = useState<RightsRequest>({ canEdit: user.canEdit, isAdmin: user.isAdmin });
  const rightsChanged = rights.canEdit !== user.canEdit || rights.isAdmin !== user.isAdmin;

  const sendLevel = (event: FormEvent) => {
    event.preventDefault();
    onLevel(chosenLevel);
  };

  const sendRights = (event: FormEvent) => {
    event.preventDefault();
    onRights(rights);
  };

  return (
    <div className="panel stack">
      <h2>{t.userTitle(user.displayName ?? user.account, user.account)}</h2>
      <form className="stack" onSubmit={sendLevel}>
        <fieldset className="stack" disabled={sending}>
          <legend>{t.passwordTitle}</legend>
          <p className="muted">{t.passwordIntro}</p>
          {user.isAdmin && <p className="muted">{t.adminLevel}</p>}
          <LevelField
            id={`${id}-level`}
            levels={levels}
            choices={choices}
            level={chosenLevel}
            problem={undefined}
            onChange={setChosenLevel}
          />
          <div>
            <button type="submit" disabled={sending}>
              {sending ? t.givingPassword : t.givePassword}
            </button>
          </div>
        </fieldset>
      </form>
      <form className="stack" onSubmit={sendRights}>
        <fieldset className="stack" disabled={sending}>
          <legend>{t.rightsTitle}</legend>
          <label className="check">
            <input
              type="checkbox"
              checked={rights.canEdit}
              onChange={(event) => setRightsForm({ ...rights, canEdit: event.target.checked })}
            />
            {t.editorField}
          </label>
          <label className="check">
            <input
              type="checkbox"
              checked={rights.isAdmin}
              onChange={(event) => setRightsForm({ ...rights, isAdmin: event.target.checked })}
            />
            {t.admin}
          </label>
          {rights.isAdmin && !user.isAdmin && <p className="muted">{t.makeAdmin}</p>}
          <div>
            <button type="submit" disabled={sending || !rightsChanged}>
              {sending ? t.savingRights : t.saveRights}
            </button>
          </div>
        </fieldset>
      </form>
      <div>
        <button type="button" onClick={onClose} disabled={sending}>
          {t.close}
        </button>
      </div>
    </div>
  );
}

/**
 * The temporary password of the last action, shown once (design 7.6): it stays in the state only while this screen is
 * open, and "Close" takes it away (kiler-ui, "Secret values").
 */
function TemporaryPasswordView({
  temporary,
  levels,
  onClose,
}: {
  temporary: TemporaryPassword;
  levels: AdminLevel[];
  onClose: () => void;
}) {
  const t = texts.temporaryPassword;
  const keys = keysGiven(temporary.level, levels)
    .map((entry) => levelLabel(entry.level, levels))
    .join(', ');
  return (
    <section className="card stack">
      <h1>{t.title}</h1>
      <p>{t.intro(temporary.displayName, temporary.account)}</p>
      <Secret value={temporary.password} copyFailed={t.copyFailed} />
      {keys !== '' && <p>{t.keys(keys)}</p>}
      <p className="muted">{t.next}</p>
      <div>
        <button type="button" className="primary" onClick={onClose}>
          {t.close}
        </button>
      </div>
    </section>
  );
}

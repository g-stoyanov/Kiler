import { browse } from './texts/browse.ts';
import { code } from './texts/code.ts';
import { form } from './texts/form.ts';
import { history } from './texts/history.ts';
import { record } from './texts/record.ts';

/**
 * All the texts of the UI, in one place (decided in M1.7). The app is in English only (PLAN section 8), so there is no
 * translation layer: the components read the texts from here. Each area of M6 keeps its texts in a file of its own in
 * `texts/`, so two sessions never write one file (decided at the start of M6).
 */
export const texts = {
  appName: 'Kiler',
  frame: {
    trash: 'Trash',
    locked: 'Kiler has locked, so this page shows only level 0 again. Sign in to read the rest.',
    version: (version: string) => `Kiler ${version}`,
  },
  theme: {
    label: 'Theme',
    light: 'Light',
    dark: 'Dark',
    system: 'System',
  },
  connection: {
    connecting: 'Connecting…',
    connected: 'Connected',
    stopped: 'Stopped',
    stoppedMessage: 'Kiler has stopped. Start Kiler again to go on.',
  },
  account: {
    guest: 'Guest',
    signIn: 'Sign in',
    password: 'Password',
    lock: 'Lock',
    lockFailed: 'Kiler did not lock. Try again.',
  },
  session: {
    reading: 'Reading the session…',
    tryAgain: 'Try again',
  },
  data: {
    reading: 'Asking Kiler about its data…',
    folder: (folder: string) => `Data folder: ${folder}`,
    noFolder:
      'No data folder has been chosen. Start Kiler from the shared folder, or give it the data folder with --data.',
    opening:
      'The data folder is opening. When its file server does not answer, this takes about 40 seconds, and then Kiler works from its local copy.',
    unusable:
      'The data folder cannot be used: it is a link, or a file has its name. The console of Kiler says which. Start Kiler with another data folder.',
    inUse:
      'Another Kiler of this Windows account has the local copy of this data folder open. Use that Kiler, or close it and start this one again.',
    indexUnreadable: 'The index of the local copy cannot be read now. Start Kiler again.',
    failed: 'The local copy cannot be made or locked. The console of Kiler says why.',
    offline:
      'The shared folder cannot be reached. Kiler shows its local copy, and nothing can be saved until the folder is back.',
    offlineNew:
      'The shared folder cannot be reached, and this computer has no copy of its data yet. Kiler opens it once the folder is back.',
    levels: (reason: string) => `levels.json: ${reason}`,
    settings: (reason: string) => `settings.json: ${reason}`,
    levelsDamaged:
      'No one can sign in, as Kiler cannot check the keys without levels.json. An admin restores it from its history.',
    settingsDamaged:
      'Kiler uses its defaults: no guest mode, and the lock after 30 minutes. An admin restores the file.',
    halfMade:
      'When an admin can sign in, an admin restores the file from its history. Only when the wizard has just stopped halfway and no one can sign in: stop Kiler, delete the new data folder and its local copy, and start Kiler again for the wizard.',
  },
  wizard: {
    title: 'New Kiler',
    intro:
      'This data folder has no Kiler yet. Set its levels and the first admin, who is you, the current Windows account. Kiler makes a key for each level from 1.',
    levels: 'Levels',
    levelsHint:
      'Level 0 is not encrypted: anyone who can open the data folder reads it. A user of a level reads every level up to it. The top level is the admin’s.',
    levelName: (level: number) => `Name of level ${level}`,
    addLevel: 'Add a level',
    removeLevel: 'Remove the top level',
    admin: 'The first admin',
    displayName: 'Display name',
    password: 'Password',
    repeat: 'Password again',
    settings: 'Settings',
    guestMode: 'Guest mode: anyone who starts Kiler reads level 0 without signing in',
    autoLockMinutes: 'Lock after this many minutes without activity',
    make: 'Make the new Kiler',
    making: 'Making the keys…',
    problems: {
      levelCount: 'There are 2 to 10 levels: 0 and a top level from 1 to 9.',
      levelBlank: 'A level needs a name.',
      levelLong: 'A name has at most 40 characters.',
      levelTwice: 'Another level has this name.',
      displayNameBlank: 'The admin needs a display name.',
      displayNameLong: 'A display name has at most 100 characters.',
      password: 'A password has 8 to 1024 characters.',
      repeat: 'The two passwords differ.',
      autoLockMinutes: 'Whole minutes from 1 to 1440.',
    },
    keyTitle: 'The recovery key',
    keyIntro:
      'Kiler shows this key only now. It restores admin access when every admin is gone. Without it, if the only admin forgets the password, the data of the top level is lost for good.',
    keyKeep:
      'Keep it outside Kiler: in a sealed envelope in a safe, or in the company password manager. Add a second admin soon.',
    copyFailed: 'The browser did not copy it. Select the key and copy it by hand.',
    saved: 'I have saved the recovery key outside Kiler',
    continue: 'Continue to the sign-in',
  },
  copy: {
    copy: 'Copy',
    copying: 'Copying…',
    copied: 'Copied',
    failed: 'The browser did not copy it. Copy it by hand.',
  },
  field: {
    empty: 'No value',
    secret: '••••',
    locked: (level: string) => `🔒 requires level ${level}`,
    unreadable: 'Cannot be read',
    unreadableHint: 'Its part of the file is damaged, or it was moved from another record.',
    gone: 'The record has gone',
    local:
      'A local address: Kiler does not open it, as the browser would send it the sign-in of Kiler. Copy it instead.',
  },
  signIn: {
    title: 'Sign in',
    lockedTitle: 'Kiler is locked',
    locked: 'Kiler has locked and dropped what this page showed. Sign in to unlock it.',
    account: (account: string) => `Windows account: ${account}`,
    noAccount: 'The name of this Windows account cannot be the name of a Kiler account, so it cannot sign in.',
    password: 'Password',
    submit: 'Sign in',
    signingIn: 'Signing in…',
    unlock: 'Unlock',
    unlocking: 'Unlocking…',
  },
  password: {
    title: 'Change your password',
    forcedTitle: 'Choose your password',
    forcedIntro: 'You signed in with a temporary password. Choose a password of your own before anything else.',
    current: 'Current password',
    password: 'New password',
    repeat: 'New password again',
    submit: 'Change the password',
    sending: 'Changing the password…',
    changed: 'Your password has been changed.',
    signInAgain: 'Sign in again',
    problems: {
      current: 'Give your current password.',
      password: 'A password has 8 to 1024 characters.',
      repeat: 'The two passwords differ.',
    },
  },
  admin: {
    users: 'Users',
    levels: 'Levels',
    reading: 'Reading the files of the team…',
    tryAgain: 'Try again',
    signInAgain: 'Sign in again',
  },
  users: {
    title: 'Users',
    fewAdmins:
      'The team has fewer than two admins. Make a second admin: if the only admin forgets the password or leaves, only the recovery key gives admin access back.',
    none: 'No user files.',
    account: 'Account',
    displayName: 'Name',
    level: 'Level',
    rights: 'Rights',
    marks: 'Marks',
    editor: 'Editor',
    admin: 'Admin',
    you: 'You',
    temporary: 'Temporary password',
    temporaryHint: 'The user has not changed the temporary password yet.',
    keysMismatch: 'Keys do not match the level',
    keysMismatchHint:
      'The file has a level without its key, or a key above it: someone may have changed the file by hand. A new temporary password gives the keys of the level that you choose.',
    unreadable: (reason: string) => `Cannot be read: ${reason}`,
    change: 'Change',
    add: 'Add a user',
    close: 'Close',
    newTitle: 'New user',
    accountField: 'Windows user name',
    accountHint: 'As the user signs in to Windows, with or without the domain. Kiler makes the account of it.',
    nameField: 'Display name',
    levelField: 'Level',
    editorField: 'Editor: makes and changes records up to their level',
    create: 'Add the user',
    creating: 'Adding the user…',
    keys: (levels: string) => `The temporary password gives the keys of: ${levels}.`,
    userTitle: (name: string, account: string) => `${name} (${account})`,
    passwordTitle: 'New temporary password',
    passwordIntro:
      'For a change of level, and for a forgotten password. The level of the file is chosen; a user who has written another level into the file gets only the keys of the level that you choose.',
    adminLevel: 'An admin has the top level. Take the admin right away first to lower it.',
    givePassword: 'Make a new temporary password',
    givingPassword: 'Making the password…',
    rightsTitle: 'Rights',
    makeAdmin: 'An admin gets the top level, with a new temporary password.',
    saveRights: 'Save the rights',
    savingRights: 'Saving the rights…',
    saved: (name: string) => `The rights of ${name} are saved.`,
    problems: {
      account: 'Give the Windows user name.',
      displayNameBlank: 'The user needs a display name.',
      displayNameLong: 'A display name has at most 100 characters.',
      level: 'Choose a level.',
    },
  },
  temporaryPassword: {
    title: 'The temporary password',
    intro: (name: string, account: string) =>
      `This is the temporary password of ${name} (${account}). Kiler shows it only now. Hand it over in person or by chat: Kiler sends nothing.`,
    next: 'At the sign-in, the user chooses a password of their own before anything else.',
    keys: (levels: string) => `It gives the keys of: ${levels}.`,
    copyFailed: 'The browser did not copy it. Select the password and copy it by hand.',
    close: 'I have it. Close',
  },
  levels: {
    title: 'Levels',
    intro:
      'Here a level gets a new name. Its key, and who holds it, stay as they are. A new level, with its key, comes in a later version of Kiler.',
    name: (level: number) => `Name of level ${level}`,
    rename: 'Rename',
    renaming: 'Renaming…',
    renamed: (level: number) => `Level ${level} is renamed.`,
    problems: {
      blank: 'A level needs a name.',
      long: 'A name has at most 40 characters.',
      twice: 'Another level has this name.',
    },
  },
  browse,
  record,
  form,
  code,
  history,
  errors: {
    unauthorized: 'This page is not signed in to Kiler. Start Kiler again, and it opens a page that is.',
    stopping: 'Kiler is stopping.',
    status: (status: number) => `Kiler answered with an error (HTTP ${status}).`,
    network: 'Kiler does not answer. It may have stopped.',
    invalid: 'Kiler sent an answer that this page cannot read.',
    unexpected: 'Something went wrong on this page. Reload it.',
  },
} as const;

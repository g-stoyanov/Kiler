/** The texts of the stash screen (M6.23), which `texts.ts` gives as `texts.stashes`. */
export const stashes = {
  title: 'Stashes',
  intro:
    'The shared stashes, each with the number of the shared records in it. A new name shows at once in every path. A merge puts the records of a stash into another, such as two stashes made for the same thing, and a stash that no shared record is in can be deleted.',
  reading: 'Reading the stashes…',
  none: 'No stashes yet. An editor makes one in the form of a record.',
  name: 'Name',
  records: 'Shared records',
  actions: 'Actions',
  rename: 'Rename',
  merge: 'Merge',
  // The buttons of a row show "Rename", "Merge into…" and "Delete", with the name of the stash for a screen reader only:
  // "Merge Payments into…".
  mergeTail: 'into…',
  delete: 'Delete',
  cancel: 'Cancel',
  renameTitle: (name: string) => `Rename “${name}”`,
  newName: 'New name',
  renaming: 'Renaming…',
  mergeTitle: (name: string) => `Merge “${name}” into another stash`,
  into: 'Merge into',
  chooseInto: 'Choose a stash',
  mergeIntro: (name: string) =>
    `Each record whose path has “${name}” shows at the stash that you choose, in the same place, and “${name}” goes from the list. A merge cannot be undone here.`,
  merging: 'Merging…',
  deleteTitle: (name: string) => `Delete “${name}”`,
  deleteIntro:
    'No shared record is in this stash. A record of the trash that has it shows at the stash before it once it is restored.',
  deleting: 'Deleting…',
  renamed: (old: string, name: string) => `“${old}” is now “${name}”.`,
  merged: (name: string, into: string) => `“${name}” is merged into “${into}”.`,
  deleted: (name: string) => `“${name}” is deleted.`,
  problems: {
    blank: 'A stash needs a name.',
    twice: 'Another stash has this name.',
  },
} as const;

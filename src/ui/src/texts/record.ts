/** The texts of the record view (M6.5), which `texts.ts` gives as `texts.record`. */
export const record = {
  reading: 'Reading the record…',
  tryAgain: 'Try again',
  notThere: 'Kiler has no record at this address. It may have been deleted, or the address is wrong.',
  toMain: 'To the main view',
  path: 'Stash path',
  unknownStash: 'A stash that cannot be read',
  noTemplate: (template: string) =>
    `Its template, ${template}, is missing or does not fit, so Kiler shows only the header of the record.`,
  description: 'Description',
  tags: 'Tags',
  created: (who: string, when: string) => `Created by ${who}, ${when}`,
  changed: (who: string, when: string) => `Last changed by ${who}, ${when}`,
  edit: 'Edit',
  copyLabel: (label: string) => `Copy ${label}`,
  show: 'Show',
  showing: 'Showing…',
  hide: 'Hide',
  showLabel: (label: string) => `Show ${label}`,
  hideLabel: (label: string) => `Hide ${label}`,
  hidesAfter: (seconds: number) => `It hides again after ${String(seconds)} seconds.`,
  copyFailed: 'The browser did not copy it. Show the value and copy it by hand.',
} as const;

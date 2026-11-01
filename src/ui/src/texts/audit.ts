/** The texts of the audit screen (M6.19), which `texts.ts` gives as `texts.audit`. */
export const audit = {
  title: 'Audit',
  intro:
    'Kiler records here each time someone shows or copies a password of the top level. Anyone who can write the shared folder can change its files, so the audit is for tracing only.',
  day: 'Day',
  previous: 'Previous',
  next: 'Next',
  reading: 'Reading the audit…',
  tryAgain: 'Try again',
  none: 'No one showed or copied a password of the top level on this day.',
  skipped: (count: number) =>
    count === 1
      ? 'A line or file of the audit for this day could not be read, so the table may miss a view.'
      : `${String(count)} lines or files of the audit for this day could not be read, so the table may miss views.`,
  time: 'Time',
  account: 'Account',
  record: 'Record',
  field: 'Field',
  use: 'View',
  uses: {
    show: 'Show',
    copy: 'Copy',
  },
} as const;

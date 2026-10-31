/** The texts of the code (M6.9, M6.15), which `texts.ts` gives as `texts.code`. */
export const code = {
  format: 'Format',
  asSaved: 'Show as saved',
  formatFailed: (reason: string) => `The SQL cannot be formatted. ${reason}`,
  parameters: 'Parameters',
  parametersHint:
    "A number goes into the query as it is, and any other value in quotes; a value typed in quotes, such as '1234567', goes in as typed. A parameter without a value stays as it is.",
  filled: 'the query with these values',
  copyFilledLabel: 'Copy the query with these values',
} as const;

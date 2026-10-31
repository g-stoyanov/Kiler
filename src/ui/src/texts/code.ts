/** The texts of the code (M6.9, M6.15), which `texts.ts` gives as `texts.code`. */
export const code = {
  format: 'Format',
  asSaved: 'Show as saved',
  formatFailed: (reason: string) => `The SQL cannot be formatted. ${reason}`,
} as const;

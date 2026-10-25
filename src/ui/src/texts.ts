/**
 * All the texts of the UI, in one place (decided in M1.7). The app is in English only (PLAN section 8), so there is no
 * translation layer: the components read the texts from here.
 */
export const texts = {
  appName: 'Kiler',
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
  home: {
    title: 'Kiler server',
    loading: 'Asking Kiler…',
    version: (version: string) => `Server version ${version}`,
  },
  errors: {
    unauthorized: 'This page is not signed in to Kiler. Start Kiler again, and it opens a page that is.',
    stopping: 'Kiler is stopping.',
    status: (status: number) => `Kiler answered with an error (HTTP ${status}).`,
    network: 'Kiler does not answer. It may have stopped.',
    invalid: 'Kiler sent an answer that this page cannot read.',
    unexpected: 'Something went wrong on this page. Reload it.',
  },
} as const;

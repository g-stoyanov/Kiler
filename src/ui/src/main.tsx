import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
// The shared CSS before App, which imports the CSS of each screen, so a rule of a screen wins its ties with it (M6.3).
import './index.css';
import { App } from './App.tsx';
import { applyThemeChoice, readThemeChoice } from './theme.ts';

// Before the first render, so React renders in the saved theme. The paint before this module runs follows the system.
applyThemeChoice(readThemeChoice());

const root = document.getElementById('root');
if (root === null) {
  throw new Error('index.html has no #root element.');
}

createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>,
);

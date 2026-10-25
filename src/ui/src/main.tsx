import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App.tsx';
import './index.css';
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

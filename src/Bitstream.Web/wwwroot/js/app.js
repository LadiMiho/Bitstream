/**
 * Application shell.
 *
 * Scaffold stage: wires the page furniture and reports whether the API is reachable. The
 * module views (activation requests, complaint tickets, reporting, administration) are added
 * under js/views/ as each module is built, and registered in the navigation below.
 */

import { api, ApiError } from './api.js';

/** @param {string} selector */
function el(selector) {
  return document.querySelector(selector);
}

async function reportApiStatus() {
  const target = el('[data-role="api-status"]');
  if (!target) {
    return;
  }

  try {
    await api.get('/health/ready');
    target.textContent = 'reachable';
    target.className = 'mt-1 text-sm text-state-done';
  } catch (error) {
    const suffix = error instanceof ApiError ? ` (${error.status})` : '';
    target.textContent = `unreachable${suffix}`;
    target.className = 'mt-1 text-sm text-state-blocked';
  }
}

function init() {
  // Nothing renders a control the session is not permitted to use, but that is presentation
  // only — authorisation is decided server-side on every call (TR-SEC-17).
  reportApiStatus();
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', init, { once: true });
} else {
  init();
}

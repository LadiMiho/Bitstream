/**
 * Application shell.
 *
 * Wires the page furniture — navigation, the signed-in user's name, sign-out — and the
 * client-side router. Each module (access management, activation requests, post-activation
 * support, reporting) is one view under js/views/, registered below and replaced with the
 * real screen as it is built. GUI-1 stage: every module view is a placeholder and the login
 * page is a stand-in for the guard to redirect to; GUI-2 replaces the login view with the
 * real sign-in flow.
 */

import { api, ApiError } from './api.js';
import { route, notFound, guardWith, start, navigate } from './router.js';
import { getSessionUser, forgetSession, requireSession } from './auth-guard.js';
import renderLogin from './views/login.js';
import renderAccessManagement from './views/access-management.js';
import renderActivationRequests from './views/activation-requests.js';
import renderPostActivation from './views/post-activation.js';
import renderReporting from './views/reporting.js';
import renderNotFound from './views/not-found.js';

const NAV_ITEMS = [
  { path: '/access-management', label: 'Access Management' },
  { path: '/activation-requests', label: 'Activation Requests' },
  { path: '/post-activation', label: 'Post-Activation Support' },
  { path: '/reporting', label: 'Reporting' }
];

/** @param {string} selector */
function el(selector) {
  return document.querySelector(selector);
}

function renderNav() {
  const nav = el('[data-role="main-nav"]');
  if (!nav) {
    return;
  }

  // TR-SEC-17: this only decides what is offered to click. Every module's own calls are
  // authorised server-side regardless of whether a link to it was ever rendered.
  nav.innerHTML = NAV_ITEMS.map(
    (item) => `<a class="nav-link" href="#${item.path}">${item.label}</a>`
  ).join('');
}

function syncSessionUi() {
  // No network call here: the guard already fetched the session for this navigation, and
  // this just reflects that same result into the header (TR-SEC-17 — presentation only).
  const user = getSessionUser();
  const nameEl = el('[data-role="user-name"]');
  const signOutButton = el('[data-action="sign-out"]');

  if (nameEl) {
    nameEl.textContent = user ? `${user.fullName} · ${user.role}` : '';
  }
  if (signOutButton) {
    signOutButton.hidden = !user;
  }
}

async function signOut() {
  try {
    await api.post('/api/v1/auth/logout');
  } catch (error) {
    if (!(error instanceof ApiError)) {
      throw error;
    }
    // Sign-out proceeds client-side either way — there is nothing left to do locally if the
    // server call itself failed, and staying "signed in" on a broken logout would be worse.
  }

  forgetSession();
  syncSessionUi();
  navigate('/login');
}

function wireSignOut() {
  const signOutButton = el('[data-action="sign-out"]');
  signOutButton?.addEventListener('click', () => {
    signOut();
  });
}

function registerRoutes() {
  route('/', renderAccessManagement);
  route('/access-management', renderAccessManagement);
  route('/activation-requests', renderActivationRequests);
  route('/post-activation', renderPostActivation);
  route('/reporting', renderReporting);
  route('/login', renderLogin);
  notFound(renderNotFound);
  guardWith(requireSession);
}

function init() {
  renderNav();
  wireSignOut();
  registerRoutes();

  const content = el('#app-content');
  start(content, { afterRender: syncSessionUi });
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', init, { once: true });
} else {
  init();
}

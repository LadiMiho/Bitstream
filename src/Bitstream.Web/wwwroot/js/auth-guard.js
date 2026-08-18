/**
 * Session lookup and the router's auth-guard.
 *
 * TR-SEC-20: the frontend's idea of "signed in" is only ever used to decide what to render —
 * hiding a page from an unauthenticated visitor is a courtesy, not access control. Every API
 * call remains authorised server-side regardless of whether this guard ran (TR-SEC-17).
 *
 * The session is re-checked against `GET /api/v1/auth/me` on every navigation rather than
 * cached across them: a session can start, expire or be revoked between route changes without
 * a full page reload, and a stale positive would leave an unauthenticated visitor looking at a
 * page whose first real API call then fails anyway.
 */

import { api, ApiError } from './api.js';
import { navigate } from './router.js';

/** Routes reachable without a session. Everything else redirects to /login. */
const PUBLIC_PATHS = new Set(['/login']);

/** The most recently fetched session user, for UI (header, nav) to read without a network call. */
let lastKnownUser = null;

async function fetchCurrentUser() {
  try {
    return await api.get('/api/v1/auth/me');
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) {
      return null;
    }
    // An unreachable API or an unexpected status is not "signed out" — let it surface rather
    // than silently redirecting to a login page that cannot explain what actually happened.
    throw error;
  }
}

/**
 * Router guard: redirects an unauthenticated visitor to /login before any module view renders,
 * and sends an already-signed-in visitor away from /login. Returns false when it has redirected,
 * so the router knows not to also render the originally-requested path.
 * @param {string} path
 * @returns {Promise<boolean>}
 */
export async function requireSession(path) {
  lastKnownUser = await fetchCurrentUser();

  if (!lastKnownUser && !PUBLIC_PATHS.has(path)) {
    navigate('/login');
    return false;
  }

  if (lastKnownUser && PUBLIC_PATHS.has(path)) {
    navigate('/');
    return false;
  }

  return true;
}

/**
 * The session user as of the guard's most recent check this render — no network call. Used by
 * the header to show the signed-in name without re-fetching what the guard just fetched.
 * @returns {object | null} The `CurrentUserResponse` body, or null when signed out.
 */
export function getSessionUser() {
  return lastKnownUser;
}

/** Forgets the last-known session, e.g. right after logout, so the UI reflects it immediately. */
export function forgetSession() {
  lastKnownUser = null;
}

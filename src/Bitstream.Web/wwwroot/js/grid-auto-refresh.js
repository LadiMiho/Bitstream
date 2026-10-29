/**
 * Automatic background refresh for a search grid (Activation Requests, Post-Activation Support).
 *
 * The page supplies how to fetch the current page of rows (with its own search, filters and
 * paging) and how to render them; this module runs every load through one place so it can:
 * reload every minute without touching open drawers, show "Updated hh:mm:ss" and a spinning
 * refresh icon while loading, ignore a response overtaken by a newer search, wait while the tab
 * is hidden or a row menu is open, pause once the user has been away for longer than the sign-in
 * idle timeout would allow (so the background calls do not keep an abandoned session alive,
 * TR-SEC-07), and stop when the session has ended.
 */
import { ApiError } from './api-client.js';

const REFRESH_INTERVAL_MS = 60 * 1000;
const AWAY_AFTER_MS = 15 * 60 * 1000;

/**
 * @param {object} options
 * @param {HTMLElement} options.statusElement   Shows "Updated …" / "Refreshing…".
 * @param {HTMLButtonElement} options.button    Refresh-now button; its svg spins while loading.
 * @param {HTMLElement} options.errorElement    The grid's error banner.
 * @param {() => Promise<any>} options.fetchPage Fetches the rows for the current search/filters/page.
 * @param {(result: any) => void} options.render Renders a fetched result.
 * @param {(error: any) => string} options.describeError
 * @param {() => boolean} options.isMenuOpen     True while a row menu is open.
 * @returns {{ load: (options?: { background?: boolean }) => Promise<void> }}
 */
export function createGridRefresher({ statusElement, button, errorElement, fetchPage, render, describeError, isMenuOpen }) {
  let timer = null;
  let lastUserActivity = Date.now();
  let sequence = 0;
  let inFlight = 0;
  let lastUpdatedAt = null;
  let stopped = false;

  function setStatus(text, { busy = false } = {}) {
    statusElement.textContent = text;
    button.querySelector('svg')?.classList.toggle('animate-spin', busy);
  }

  function showError(message) {
    errorElement.textContent = message;
    errorElement.hidden = !message;
  }

  function updatedText() {
    return lastUpdatedAt
      ? `Updated ${lastUpdatedAt.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })}`
      : '';
  }

  function schedule() {
    clearTimeout(timer);
    if (!stopped) {
      timer = setTimeout(tick, REFRESH_INTERVAL_MS);
    }
  }

  function tick() {
    if (document.hidden) {
      // Picked up again by the visibilitychange handler.
      return;
    }

    if (Date.now() - lastUserActivity > AWAY_AFTER_MS) {
      setStatus(`${updatedText()} · paused while you are away`);
      return;
    }

    if (isMenuOpen() || inFlight > 0) {
      // Re-rendering would close the open row menu; try again on the next tick.
      schedule();
      return;
    }

    load({ background: true });
  }

  function isDue() {
    return !stopped && (!lastUpdatedAt || Date.now() - lastUpdatedAt.getTime() >= REFRESH_INTERVAL_MS);
  }

  async function load({ background = false } = {}) {
    const current = ++sequence;
    clearTimeout(timer);
    inFlight++;
    setStatus('Refreshing…', { busy: true });

    try {
      const result = await fetchPage();
      if (current !== sequence) {
        // A newer search (another page, filter or search term) has started; its result wins.
        return;
      }

      showError('');
      if (background && isMenuOpen()) {
        // A row menu was opened while this was loading; keep it and refresh on the next tick.
        setStatus(updatedText());
        return;
      }

      render(result);
      lastUpdatedAt = new Date();
      setStatus(updatedText());
    } catch (error) {
      if (current !== sequence) {
        return;
      }

      if (error instanceof ApiError && error.status === 401) {
        // Signed out (idle timeout or elsewhere): stop polling until the page is reloaded.
        stopped = true;
        showError('Your session has ended. Reload the page to sign in again.');
        setStatus('Automatic refresh stopped');
      } else if (background) {
        // Keep the rows on screen; the next tick tries again.
        setStatus(`${updatedText() || 'Not loaded'} · refresh failed, retrying`);
      } else {
        showError(describeError(error));
        setStatus(updatedText() || 'Not loaded');
      }
    } finally {
      inFlight--;
      if (current === sequence) {
        schedule();
      }
    }
  }

  button.addEventListener('click', () => {
    stopped = false;
    load();
  });

  const noteActivity = () => {
    const wasAway = Date.now() - lastUserActivity > AWAY_AFTER_MS;
    lastUserActivity = Date.now();
    if (wasAway && !document.hidden && isDue()) {
      tick();
    }
  };
  for (const type of ['pointerdown', 'keydown', 'wheel', 'touchstart']) {
    document.addEventListener(type, noteActivity, { capture: true, passive: true });
  }

  document.addEventListener('visibilitychange', () => {
    if (!document.hidden && isDue()) {
      tick();
    }
  });

  return { load };
}

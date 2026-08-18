/**
 * Minimal hash-based client-side router.
 *
 * Hash routing (`#/path`) rather than the History API: the API host serves this folder as a
 * plain static file tree with no catch-all route back to index.html, so a deep link to a
 * pushState-style path would 404 on refresh. A hash never leaves the document the browser
 * already has, so every route works on a hard refresh with no server change.
 */

const routes = [];
let notFoundView = null;
let guard = null;
let contentElement = null;
let afterRenderHook = null;
let rendering = false;

/** @param {string} path Exact hash path, e.g. "/activation-requests". @param {(container: HTMLElement) => unknown} view */
export function route(path, view) {
  routes.push({ path, view });
}

/** @param {(container: HTMLElement) => unknown} view */
export function notFound(view) {
  notFoundView = view;
}

/**
 * Registers a guard run before every render.
 * @param {(path: string) => Promise<boolean> | boolean} fn Return false to cancel the render —
 *   the guard is expected to have already navigated somewhere else in that case.
 */
export function guardWith(fn) {
  guard = fn;
}

function currentPath() {
  const hash = window.location.hash;
  const path = hash.startsWith('#') ? hash.slice(1) : hash;
  return path || '/';
}

/** @param {string} path */
export function navigate(path) {
  if (currentPath() === path) {
    render();
    return;
  }
  window.location.hash = path;
}

function matchRoute(path) {
  return routes.find((candidate) => candidate.path === path) || null;
}

function markActiveNavLinks(path) {
  document.querySelectorAll('[data-role="main-nav"] a').forEach((link) => {
    const isActive = link.getAttribute('href') === `#${path}`;
    link.classList.toggle('nav-link-active', isActive);
    if (isActive) {
      link.setAttribute('aria-current', 'page');
    } else {
      link.removeAttribute('aria-current');
    }
  });
}

async function render() {
  if (!contentElement || rendering) {
    return;
  }

  rendering = true;
  try {
    const path = currentPath();

    if (guard) {
      const allowed = await guard(path);
      if (allowed === false) {
        return;
      }
    }

    const matched = matchRoute(path);
    const view = matched ? matched.view : notFoundView;

    contentElement.innerHTML = '';
    if (typeof view === 'function') {
      await view(contentElement);
    }

    markActiveNavLinks(path);

    if (afterRenderHook) {
      await afterRenderHook(path);
    }
  } finally {
    rendering = false;
  }
}

/**
 * Starts the router against a content container.
 * @param {HTMLElement} element
 * @param {{ afterRender?: (path: string) => unknown }} [options]
 */
export function start(element, options = {}) {
  contentElement = element;
  afterRenderHook = options.afterRender || null;
  window.addEventListener('hashchange', render);
  render();
}

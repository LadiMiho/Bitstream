/**
 * Thin fetch wrapper shared by every page's JSON support calls back to the controller that owns
 * it (e.g. Controllers/UsersController.cs, Controllers/AuthController.cs) — there is no separate
 * "API" here; the only real API in this solution is Bitstream.Api (CRM integration, a different
 * host entirely). This file is client-side behaviour only (TR-SEC-20 / the GUI-1 rule that
 * JavaScript never owns page-to-page navigation) — it hands back a parsed result and every page
 * decides for itself what to do with it, including any full-page redirect. No routing, no
 * view-swapping, nothing rendered by this file.
 *
 * Every call carries a correlation ID (TR-ARC-04) and includes credentials so the session
 * cookie travels on same-origin requests (TR-SEC-07). ProblemDetails responses are turned into
 * a typed error carrying field-level messages (TR-NFR-12) so a page can show exactly what was
 * wrong rather than a generic failure string.
 */

const CORRELATION_HEADER = 'X-Correlation-Id';

export class ApiError extends Error {
  /**
   * @param {string} message
   * @param {number} status
   * @param {object|null} problem ProblemDetails body, when the response carried one.
   */
  constructor(message, status, problem) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.problem = problem;
    /** Field-level validation messages, keyed by field name (RFC 7807 "errors" extension). */
    this.errors = (problem && problem.errors) || {};
  }
}

/**
 * A v4 UUID for the correlation header. crypto.randomUUID exists only on secure pages (https or
 * http://localhost); on a site opened over plain http it is undefined, so fall back to
 * crypto.getRandomValues, which every page has.
 * @returns {string}
 */
export function newCorrelationId() {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }

  const bytes = new Uint8Array(16);
  if (typeof crypto !== 'undefined' && typeof crypto.getRandomValues === 'function') {
    crypto.getRandomValues(bytes);
  } else {
    for (let i = 0; i < bytes.length; i += 1) {
      bytes[i] = Math.floor(Math.random() * 256);
    }
  }

  bytes[6] = (bytes[6] & 0x0f) | 0x40; // version 4
  bytes[8] = (bytes[8] & 0x3f) | 0x80; // RFC 4122 variant
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/**
 * @param {string} path
 * @param {{ method?: string, body?: unknown }} [options]
 * @returns {Promise<unknown>} Parsed JSON body, or null for a 204.
 */
export async function apiRequest(path, options = {}) {
  // Built before the try below, so a script error here keeps its own message instead of being
  // reported as a network failure.
  const init = {
    method: options.method || 'GET',
    credentials: 'same-origin',
    headers: {
      Accept: 'application/json',
      [CORRELATION_HEADER]: newCorrelationId(),
      ...(options.body === undefined ? {} : { 'Content-Type': 'application/json' })
    },
    body: options.body === undefined ? undefined : JSON.stringify(options.body)
  };

  let response;

  try {
    response = await fetch(path, init);
  } catch (error) {
    // No answer at all (server down, wrong address, TLS/certificate problem, connection reset):
    // status 0, with the browser's own reason kept for the log.
    throw new ApiError(`Could not reach the server: ${error && error.message ? error.message : error}`, 0, null);
  }

  if (response.status === 204) {
    return null;
  }

  const contentType = response.headers.get('Content-Type') || '';
  const payload = contentType.includes('json') ? await response.json().catch(() => null) : null;

  if (!response.ok) {
    const detail = (payload && (payload.detail || payload.title)) || response.statusText;
    throw new ApiError(detail, response.status, payload);
  }

  if (payload === null) {
    // A success status but not the JSON this call expects — typically a redirect that ended on
    // an HTML page (sign-in page, IIS error page, proxy page).
    const where = response.redirected ? ` after a redirect to ${response.url}` : '';
    throw new ApiError(`Unexpected response from the server (HTTP ${response.status}, ${contentType || 'no content type'}${where}).`, response.status, null);
  }

  return payload;
}

/**
 * Sends a browser-side failure to the server log (Controllers/ClientLogController.cs), so it
 * ends up in the log file next to the server's own entries. Fire-and-forget: never throws and
 * never sends form values — only the error itself.
 * @param {string} page
 * @param {unknown} error
 */
export function reportClientError(page, error) {
  try {
    const body = JSON.stringify({
      page,
      message: error && error.message ? String(error.message) : String(error),
      status: error instanceof ApiError ? error.status : null,
      url: window.location.pathname,
      detail: error instanceof ApiError ? null : (error && error.stack ? String(error.stack).slice(0, 500) : null)
    });

    fetch('/ClientLog', {
      method: 'POST',
      credentials: 'same-origin',
      keepalive: true,
      headers: { 'Content-Type': 'application/json' },
      body
    }).catch(() => {});
  } catch {
    // Reporting must never cause an error of its own.
  }
}

export const api = {
  /** @param {string} path */
  get: (path) => apiRequest(path),
  /** @param {string} path @param {unknown} body */
  post: (path, body) => apiRequest(path, { method: 'POST', body }),
  /** @param {string} path @param {unknown} body */
  put: (path, body) => apiRequest(path, { method: 'PUT', body }),
  /** @param {string} path @param {unknown} body */
  patch: (path, body) => apiRequest(path, { method: 'PATCH', body }),
  /** @param {string} path */
  delete: (path) => apiRequest(path, { method: 'DELETE' })
};

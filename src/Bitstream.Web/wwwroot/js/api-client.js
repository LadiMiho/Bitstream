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
 * @param {string} path
 * @param {{ method?: string, body?: unknown }} [options]
 * @returns {Promise<unknown>} Parsed JSON body, or null for a 204.
 */
export async function apiRequest(path, options = {}) {
  let response;

  try {
    response = await fetch(path, {
      method: options.method || 'GET',
      credentials: 'same-origin',
      headers: {
        Accept: 'application/json',
        [CORRELATION_HEADER]: crypto.randomUUID(),
        ...(options.body === undefined ? {} : { 'Content-Type': 'application/json' })
      },
      body: options.body === undefined ? undefined : JSON.stringify(options.body)
    });
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

/**
 * Thin fetch wrapper for the Bitstream Portal JSON API.
 *
 * This is client-side behaviour only (TR-SEC-20 / the GUI-1 rule that JavaScript never owns
 * page-to-page navigation) — it calls the existing minimal-API endpoints under /api/v1 and
 * hands back a parsed result; every page decides for itself what to do with it, including any
 * full-page redirect. No routing, no view-swapping, nothing rendered by this file.
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
 * @param {string} path Path beginning with /api.
 * @param {{ method?: string, body?: unknown }} [options]
 * @returns {Promise<unknown>} Parsed JSON body, or null for a 204.
 */
export async function apiRequest(path, options = {}) {
  const response = await fetch(path, {
    method: options.method || 'GET',
    credentials: 'same-origin',
    headers: {
      Accept: 'application/json',
      [CORRELATION_HEADER]: crypto.randomUUID(),
      ...(options.body === undefined ? {} : { 'Content-Type': 'application/json' })
    },
    body: options.body === undefined ? undefined : JSON.stringify(options.body)
  });

  if (response.status === 204) {
    return null;
  }

  const contentType = response.headers.get('Content-Type') || '';
  const payload = contentType.includes('json') ? await response.json().catch(() => null) : null;

  if (!response.ok) {
    const detail = (payload && (payload.detail || payload.title)) || response.statusText;
    throw new ApiError(detail, response.status, payload);
  }

  return payload;
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

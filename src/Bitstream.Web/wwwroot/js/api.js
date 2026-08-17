/**
 * Thin fetch wrapper for the Bitstream Portal API.
 *
 * Every call goes through here so that the cross-cutting behaviour the TRD requires is in
 * one place rather than repeated per screen:
 *   - a correlation ID on every request, echoed back and logged (TR-ARC-04);
 *   - credentials included, so the session cookie travels and expiry is handled centrally
 *     (TR-SEC-07);
 *   - ProblemDetails responses turned into a typed error carrying field-level messages
 *     (TR-NFR-12).
 *
 * No framework, no bundler: this is an ES module loaded directly by the browser.
 */

const CORRELATION_HEADER = 'X-Correlation-Id';

/** Error carrying the ProblemDetails body returned by the API. */
export class ApiError extends Error {
  /**
   * @param {string} message
   * @param {number} status
   * @param {object} problem ProblemDetails body, when the response carried one.
   * @param {string} correlationId
   */
  constructor(message, status, problem, correlationId) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.problem = problem;
    this.correlationId = correlationId;
    /** Field-level validation messages, keyed by field name. */
    this.errors = (problem && problem.errors) || {};
  }
}

function newCorrelationId() {
  return crypto.randomUUID();
}

/**
 * Issues a request against the API.
 *
 * @param {string} path Path beginning with /api.
 * @param {{ method?: string, body?: unknown, signal?: AbortSignal }} [options]
 * @returns {Promise<unknown>} Parsed JSON body, or null for 204.
 */
export async function request(path, options = {}) {
  const correlationId = newCorrelationId();

  const response = await fetch(path, {
    method: options.method || 'GET',
    credentials: 'same-origin',
    signal: options.signal,
    headers: {
      Accept: 'application/json',
      [CORRELATION_HEADER]: correlationId,
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
    // The server's correlation ID wins: it is the one written to the portal logs.
    const serverCorrelationId = response.headers.get(CORRELATION_HEADER) || correlationId;
    const detail = (payload && (payload.detail || payload.title)) || response.statusText;
    throw new ApiError(detail, response.status, payload, serverCorrelationId);
  }

  return payload;
}

export const api = {
  /** @param {string} path */
  get: (path) => request(path),
  /** @param {string} path @param {unknown} body */
  post: (path, body) => request(path, { method: 'POST', body })
};

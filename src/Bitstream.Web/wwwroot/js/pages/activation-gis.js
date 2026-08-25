/**
 * GIS verification admin screen: looks a request up (GET .../{publicId}, same read endpoint
 * the detail page uses) to get its numeric requestId, then — only when eligible — records the
 * outcome against the existing PATCH /ActivationRequests/{requestId}/gis-outcome
 * endpoint. The eligibility check here (status === 'AwaitingGisVerification') only decides
 * whether to show the form; the API is what actually enforces it and returns 409 otherwise.
 */
import { api, ApiError } from '../api-client.js';
import { presentStatus } from '../status-presentation.js';

function el(selector) {
  return document.querySelector(selector);
}

function showError(target, message) {
  target.textContent = message;
  target.hidden = !message;
}

let currentRequest = null;

function render(request) {
  currentRequest = request;

  el('#gis-detail-public-id').textContent = request.publicId;
  const status = presentStatus(request.status);
  const statusEl = el('#gis-detail-status');
  statusEl.className = status.className;
  statusEl.textContent = status.label;

  el('#gis-detail-isp-id').textContent = request.ispId;
  el('#gis-detail-package').textContent = request.packageCode;
  el('#gis-detail-location').textContent = request.locationRaw;
  el('#gis-detail-coordinates').textContent = `${request.locationLat}, ${request.locationLng}`;

  const eligible = request.status === 'AwaitingGisVerification';
  el('#gis-outcome-fields').hidden = !eligible;
  el('#gis-outcome-not-eligible').hidden = eligible;

  el('#gis-detail').hidden = false;
}

async function lookUp(publicId) {
  const errorTarget = el('#gis-lookup-error');
  showError(errorTarget, '');
  el('#gis-detail').hidden = true;

  try {
    const request = await api.get(`/ActivationRequests/${encodeURIComponent(publicId)}`);
    render(request);
  } catch (error) {
    showError(errorTarget, error instanceof ApiError && error.status === 404
      ? 'No request with that ID, or it is not one you can see.'
      : (error instanceof ApiError ? error.message : 'Something went wrong. Please try again.'));
  }
}

function init() {
  const root = el('[data-role="activation-gis-page"]');
  if (!root) {
    return;
  }

  el('#gis-lookup-form').addEventListener('submit', (event) => {
    event.preventDefault();
    lookUp(el('#gis-lookup-id').value.trim());
  });

  el('#gis-outcome-form').addEventListener('submit', async (event) => {
    event.preventDefault();
    const outcomeError = el('#gis-outcome-error');
    showError(outcomeError, '');

    const selected = el('input[name="lineAvailable"]:checked');
    if (!selected) {
      showError(outcomeError, 'Choose line exists or no line.');
      return;
    }

    const lineAvailable = selected.value === 'true';
    const reason = el('#gis-reason').value.trim() || null;

    if (!lineAvailable && !reason) {
      showError(outcomeError, 'A reason is required when recording no line (TR-ACT-13).');
      return;
    }

    const submitButton = event.target.querySelector('button[type="submit"]');
    submitButton.disabled = true;

    try {
      await api.patch(`/ActivationRequests/${currentRequest.requestId}/gis-outcome`, { lineAvailable, reason });
      const refreshed = await api.get(`/ActivationRequests/${encodeURIComponent(currentRequest.publicId)}`);
      render(refreshed);
      el('#gis-outcome-form').reset();
    } catch (error) {
      showError(outcomeError, error instanceof ApiError ? error.message : 'Something went wrong. Please try again.');
    } finally {
      submitButton.disabled = false;
    }
  });
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', init, { once: true });
} else {
  init();
}

/**
 * Activation request detail: looks a request up by public ID against the existing
 * GET /ActivationRequests/{publicId} endpoint and renders it — including the
 * integration-pending statuses (PendingCrmSync, IntegrationFailed) exactly as returned,
 * per TR-ACT-11. No status logic lives here; presentStatus only maps a label and colour.
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

function formatDate(value) {
  return value ? new Date(value).toLocaleString() : '—';
}

function render(request) {
  el('#activation-detail-public-id').textContent = request.publicId;

  const status = presentStatus(request.status);
  const statusEl = el('#activation-detail-status');
  statusEl.className = status.className;
  statusEl.textContent = status.label;

  const reasonEl = el('#activation-detail-status-reason');
  reasonEl.textContent = request.statusReason || '';
  reasonEl.hidden = !request.statusReason;

  el('#activation-detail-isp-id').textContent = request.ispId;
  el('#activation-detail-package').textContent = request.packageCode;
  el('#activation-detail-location').textContent = request.locationRaw;
  el('#activation-detail-coordinates').textContent = `${request.locationLat}, ${request.locationLng}`;
  el('#activation-detail-classification').textContent = request.classification;
  el('#activation-detail-duration').textContent = `${request.contractDurationMonths} months`;
  el('#activation-detail-sales-order').textContent = request.salesOrderId || 'Not yet raised';
  el('#activation-detail-created').textContent = formatDate(request.createdAt);
  el('#activation-detail-updated').textContent = formatDate(request.lastUpdatedAt);
  el('#activation-detail-comments').textContent = request.comments || '(none)';

  el('#activation-detail').hidden = false;
}

async function lookUp(publicId) {
  const errorTarget = el('#activation-lookup-error');
  showError(errorTarget, '');
  el('#activation-detail').hidden = true;

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
  const root = el('[data-role="activation-detail-page"]');
  if (!root) {
    return;
  }

  const form = el('#activation-lookup-form');
  form.addEventListener('submit', (event) => {
    event.preventDefault();
    lookUp(el('#activation-lookup-id').value.trim());
  });

  const prefilled = root.dataset.publicId;
  if (prefilled) {
    el('#activation-lookup-id').value = prefilled;
    lookUp(prefilled);
  }
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', init, { once: true });
} else {
  init();
}

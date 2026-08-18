/**
 * Activation request submission: a single POST to the existing
 * /api/v1/activation-requests endpoint (Bitstream.Api/Endpoints/ActivationEndpoints.cs).
 * All validation (package/classification/duration against the configured catalogue,
 * location parsing) happens server-side; this only shows whatever the API says back.
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

function describeError(error) {
  if (!(error instanceof ApiError)) {
    return 'Something went wrong. Please try again.';
  }
  const fieldMessages = Object.values(error.errors).flat();
  return fieldMessages.length > 0 ? fieldMessages.join(' ') : error.message;
}

function init() {
  const root = el('[data-role="activation-new-page"]');
  if (!root) {
    return;
  }

  const callerIspId = root.dataset.callerIspId;
  if (callerIspId) {
    const ispField = el('#activation-isp-id');
    ispField.value = callerIspId;
    ispField.readOnly = true;
  }

  const form = el('#activation-new-form');
  const errorTarget = el('#activation-new-error');

  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    showError(errorTarget, '');

    const durationRaw = el('#activation-duration').value;
    const classification = el('#activation-classification').value.trim();

    const body = {
      ispId: Number(el('#activation-isp-id').value),
      packageCode: el('#activation-package').value.trim(),
      locationRaw: el('#activation-location').value.trim(),
      classification: classification === '' ? null : classification,
      contractDurationMonths: Number(durationRaw),
      comments: el('#activation-comments').value.trim() || null
    };

    const submitButton = form.querySelector('button[type="submit"]');
    submitButton.disabled = true;

    try {
      const created = await api.post('/api/v1/activation-requests', body);
      form.hidden = true;

      const status = presentStatus(created.status);
      el('#activation-new-public-id').textContent = created.publicId;
      el('#activation-new-status').innerHTML = '';
      const pill = document.createElement('span');
      pill.className = status.className;
      pill.textContent = status.label;
      el('#activation-new-status').appendChild(pill);
      el('#activation-new-view-link').href = `/ActivationRequests/Detail?publicId=${encodeURIComponent(created.publicId)}`;
      el('#activation-new-result').hidden = false;
    } catch (error) {
      showError(errorTarget, describeError(error));
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

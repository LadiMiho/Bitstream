/**
 * ISP administration behaviour: create, look up by ID, lock/unlock — each a direct call to the
 * existing /api/v1/isps endpoints (Bitstream.Web/Endpoints/AdministrationEndpoints.cs). No
 * backend logic is duplicated here; every validation message shown comes from the API's own
 * response.
 */
import { api, ApiError } from '../api-client.js';

function el(selector) {
  return document.querySelector(selector);
}

function showError(target, message) {
  if (!target) {
    return;
  }
  target.textContent = message;
  target.hidden = !message;
}

function describeError(error) {
  if (!(error instanceof ApiError)) {
    return 'Something went wrong. Please try again.';
  }
  const fieldMessages = Object.values(error.errors).flat();
  if (fieldMessages.length > 0) {
    return fieldMessages.join(' ');
  }
  return error.message;
}

function renderIsp(isp) {
  el('#isp-detail-id').textContent = isp.ispId;
  el('#isp-detail-status').textContent = isp.status;
  el('#isp-detail-name').textContent = isp.name;
  el('#isp-detail-nipt').textContent = isp.nipt;
  el('#isp-detail-contact-person').textContent = isp.contactPerson;
  el('#isp-detail-contact-email').textContent = isp.contactEmail;
  el('#isp-detail-contact-mobile').textContent = isp.contactMobile;
  el('#isp-detail-crm-bp').textContent = isp.crmBpReference;
  el('#isp-detail').hidden = false;
  el('#isp-detail').dataset.ispId = isp.ispId;
}

async function setStatus(ispId, status) {
  const statusError = el('#isp-status-error');
  showError(statusError, '');

  try {
    await api.patch(`/api/v1/isps/${ispId}/status`, { status });
    const isp = await api.get(`/api/v1/isps/${ispId}`);
    renderIsp(isp);
  } catch (error) {
    showError(statusError, describeError(error));
  }
}

function init() {
  const root = el('[data-role="isp-admin-page"]');
  if (!root) {
    return;
  }

  const createForm = el('#isp-create-form');
  createForm?.addEventListener('submit', async (event) => {
    event.preventDefault();
    const createError = el('#isp-create-error');
    showError(createError, '');

    const body = {
      name: el('#isp-create-name').value.trim(),
      nipt: el('#isp-create-nipt').value.trim(),
      contactPerson: el('#isp-create-contact-person').value.trim(),
      contactEmail: el('#isp-create-contact-email').value.trim(),
      contactMobile: el('#isp-create-contact-mobile').value.trim(),
      crmBpReference: el('#isp-create-crm-bp').value.trim()
    };

    try {
      const isp = await api.post('/api/v1/isps', body);
      createForm.reset();
      el('#isp-lookup-id').value = isp.ispId;
      renderIsp(isp);
    } catch (error) {
      showError(createError, describeError(error));
    }
  });

  const lookupForm = el('#isp-lookup-form');
  lookupForm.addEventListener('submit', async (event) => {
    event.preventDefault();
    const lookupError = el('#isp-lookup-error');
    showError(lookupError, '');
    el('#isp-detail').hidden = true;

    const ispId = el('#isp-lookup-id').value;

    try {
      const isp = await api.get(`/api/v1/isps/${ispId}`);
      renderIsp(isp);
    } catch (error) {
      showError(lookupError, error instanceof ApiError && error.status === 404
        ? 'No ISP with that ID, or it is not one you can see.'
        : describeError(error));
    }
  });

  el('#isp-lock-button')?.addEventListener('click', () => {
    const ispId = el('#isp-detail').dataset.ispId;
    setStatus(ispId, 'Locked');
  });

  el('#isp-unlock-button')?.addEventListener('click', () => {
    const ispId = el('#isp-detail').dataset.ispId;
    setStatus(ispId, 'Active');
  });
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', init, { once: true });
} else {
  init();
}

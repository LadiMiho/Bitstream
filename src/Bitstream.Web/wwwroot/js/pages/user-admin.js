/**
 * User administration behaviour: create, look up by ID, lock/unlock — each a direct call to
 * the existing /api/v1/users endpoints (Bitstream.Web/Endpoints/AdministrationEndpoints.cs).
 * No backend logic is duplicated here; every validation message shown comes from the API's own
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

function renderUser(user) {
  el('#user-detail-id').textContent = user.userId;
  el('#user-detail-status').textContent = user.status;
  el('#user-detail-full-name').textContent = user.fullName;
  el('#user-detail-email').textContent = user.email;
  el('#user-detail-mobile').textContent = user.mobile;
  el('#user-detail-role').textContent = user.role;
  el('#user-detail-isp-id').textContent = user.ispId ?? '(internal user)';
  el('#user-detail-last-login').textContent = user.lastLoginAt
    ? new Date(user.lastLoginAt).toLocaleString()
    : 'Never';
  el('#user-detail').hidden = false;
  el('#user-detail').dataset.userId = user.userId;
}

async function setStatus(userId, status) {
  const statusError = el('#user-status-error');
  showError(statusError, '');

  try {
    await api.patch(`/api/v1/users/${userId}/status`, { status });
    const user = await api.get(`/api/v1/users/${userId}`);
    renderUser(user);
  } catch (error) {
    showError(statusError, describeError(error));
  }
}

function init() {
  const root = el('[data-role="user-admin-page"]');
  if (!root) {
    return;
  }

  const createForm = el('#user-create-form');
  createForm?.addEventListener('submit', async (event) => {
    event.preventDefault();
    const createError = el('#user-create-error');
    showError(createError, '');

    const ispIdRaw = el('#user-create-isp-id').value.trim();

    const body = {
      ispId: ispIdRaw === '' ? null : Number(ispIdRaw),
      fullName: el('#user-create-full-name').value.trim(),
      email: el('#user-create-email').value.trim(),
      mobile: el('#user-create-mobile').value.trim(),
      roleName: el('#user-create-role').value,
      initialPassword: el('#user-create-password').value
    };

    try {
      const user = await api.post('/api/v1/users', body);
      createForm.reset();
      el('#user-lookup-id').value = user.userId;
      renderUser(user);
    } catch (error) {
      showError(createError, describeError(error));
    }
  });

  const lookupForm = el('#user-lookup-form');
  lookupForm.addEventListener('submit', async (event) => {
    event.preventDefault();
    const lookupError = el('#user-lookup-error');
    showError(lookupError, '');
    el('#user-detail').hidden = true;

    const userId = el('#user-lookup-id').value;

    try {
      const user = await api.get(`/api/v1/users/${userId}`);
      renderUser(user);
    } catch (error) {
      showError(lookupError, error instanceof ApiError && error.status === 404
        ? 'No user with that ID, or it is not one you can see.'
        : describeError(error));
    }
  });

  el('#user-lock-button')?.addEventListener('click', () => {
    const userId = el('#user-detail').dataset.userId;
    setStatus(userId, 'Locked');
  });

  el('#user-unlock-button')?.addEventListener('click', () => {
    const userId = el('#user-detail').dataset.userId;
    setStatus(userId, 'Active');
  });
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', init, { once: true });
} else {
  init();
}

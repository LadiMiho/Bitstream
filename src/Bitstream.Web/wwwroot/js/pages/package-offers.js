/**
 * Package offers screen (Controllers/PackageOffersController.cs): add an offer, change its CRM
 * code, activate or deactivate it. The table is rendered server-side, so every successful write
 * simply reloads the page. Validation and authorisation are the server's; this script only shows
 * what it returns.
 */
import { api, ApiError } from '../api-client.js';
import { openDrawer, closeDrawer, drawerBody } from '../drawer.js';

const BASE = '/ActivationRequests/PackageOffers';

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

/** TR-NFR-12: each server-reported violation next to its field, the rest on the form's banner. */
function showFieldErrors(form, error) {
  const generalTarget = form.querySelector('[data-field-error="request"]');
  form.querySelectorAll('[data-field-error]').forEach((target) => showError(target, ''));

  if (!(error instanceof ApiError)) {
    showError(generalTarget, 'Something went wrong. Please try again.');
    return;
  }

  const unmatched = [];
  for (const [field, messages] of Object.entries(error.errors)) {
    const target = form.querySelector(`[data-field-error="${field}"]`);
    if (target) {
      showError(target, messages.join(' '));
    } else {
      unmatched.push(...messages);
    }
  }

  if (unmatched.length > 0) {
    showError(generalTarget, unmatched.join(' '));
  } else if (Object.keys(error.errors).length === 0) {
    showError(generalTarget, error.message);
  }
}

// --- Confirmation popup (Views/Shared/_Layout.cshtml) -------------------------------------
function confirmAction(message) {
  const modal = el('#confirm-modal');
  const backdrop = el('#confirm-backdrop');
  const ok = el('#confirm-ok');
  const cancel = el('#confirm-cancel');

  el('#confirm-message').textContent = message;
  modal.hidden = false;
  backdrop.hidden = false;

  return new Promise((resolve) => {
    const cleanup = (result) => {
      modal.hidden = true;
      backdrop.hidden = true;
      ok.removeEventListener('click', onOk);
      cancel.removeEventListener('click', onCancel);
      backdrop.removeEventListener('click', onCancel);
      resolve(result);
    };
    const onOk = () => cleanup(true);
    const onCancel = () => cleanup(false);

    ok.addEventListener('click', onOk);
    cancel.addEventListener('click', onCancel);
    backdrop.addEventListener('click', onCancel);
  });
}

function offerPath(button) {
  return `${BASE}/${encodeURIComponent(button.dataset.packageCode)}/${button.dataset.months}`;
}

async function setActive(button, isActive) {
  const verb = isActive ? 'Activate' : 'Deactivate';
  const consequence = isActive
    ? 'It will be offered again on the New activation request form.'
    : 'It will no longer be offered on the New activation request form; existing requests are not affected.';

  if (!(await confirmAction(`${verb} ${button.dataset.label}? ${consequence}`))) {
    return;
  }

  try {
    await api.patch(`${offerPath(button)}/status`, { isActive });
    window.location.reload();
  } catch (error) {
    showError(el('#offers-error'), error instanceof ApiError ? error.message : 'Something went wrong. Please try again.');
  }
}

drawerBody.addEventListener('submit', async (event) => {
  const form = event.target;
  if (!(form instanceof HTMLFormElement)) {
    return;
  }
  event.preventDefault();
  form.querySelectorAll('[data-field-error]').forEach((target) => showError(target, ''));

  try {
    if (form.dataset.action === 'create') {
      await api.post(BASE, {
        packageCode: form.querySelector('[name=packageCode]').value,
        contractDurationMonths: Number(form.querySelector('[name=contractDurationMonths]').value),
        offerCode: form.querySelector('[name=offerCode]').value.trim()
      });
    } else if (form.dataset.action === 'update') {
      await api.put(`${BASE}/${encodeURIComponent(form.dataset.packageCode)}/${form.dataset.months}`, {
        offerCode: form.querySelector('[name=offerCode]').value.trim()
      });
    }

    closeDrawer();
    window.location.reload();
  } catch (error) {
    showFieldErrors(form, error);
  }
});

function init() {
  const root = el('[data-role="package-offers-page"]');
  if (!root) {
    return;
  }

  el('#offer-add-button').addEventListener('click', () => openDrawer('Add package offer', `${BASE}/AddDrawer`));

  root.addEventListener('click', (event) => {
    const button = event.target.closest('[data-offer-action]');
    if (!button) {
      return;
    }

    switch (button.dataset.offerAction) {
      case 'edit':
        openDrawer(`Edit offer — ${button.dataset.label}`, `${offerPath(button)}/EditDrawer`);
        break;
      case 'activate':
        setActive(button, true);
        break;
      case 'deactivate':
        setActive(button, false);
        break;
      default:
        break;
    }
  });
}

init();

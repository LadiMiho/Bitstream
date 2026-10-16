/**
 * Support classifications screen (Controllers/SupportClassificationsController.cs): add, edit,
 * activate or deactivate an item of the three-level hierarchy. The columns are rendered
 * server-side and the selection lives in the query string (?l1=&l2=), so every successful write
 * reloads the page with the same selection. Validation and authorisation are the server's; this
 * script only shows what it returns.
 */
import { api, ApiError, reportClientError } from '../api-client.js';
import { openDrawer, closeDrawer, drawerBody } from '../drawer.js';

const BASE = '/PostActivation/Classifications';

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
    reportClientError('support-classifications', error);
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

function formBody(form) {
  return {
    code: form.querySelector('[name=code]').value.trim(),
    name: form.querySelector('[name=name]').value.trim(),
    displayOrder: Number(form.querySelector('[name=displayOrder]').value || 0)
  };
}

async function setActive(button, isActive) {
  const label = button.dataset.label;
  const hasChildren = Number(button.dataset.level) < 3;
  const message = isActive
    ? `Activate "${label}"?`
    : `Deactivate "${label}"? It will no longer be offered on the request form${hasChildren ? ', and neither will the items under it' : ''}. Existing requests are not affected.`;

  if (!(await confirmAction(message))) {
    return;
  }

  try {
    await api.patch(`${BASE}/${button.dataset.id}/status`, { isActive });
    window.location.reload();
  } catch (error) {
    if (!(error instanceof ApiError)) {
      reportClientError('support-classifications', error);
    }
    showError(el('#classification-error'), error instanceof ApiError ? error.message : 'Something went wrong. Please try again.');
  }
}

drawerBody.addEventListener('submit', async (event) => {
  const form = event.target;
  if (!(form instanceof HTMLFormElement) || !form.id.startsWith('classification-')) {
    return;
  }
  event.preventDefault();
  form.querySelectorAll('[data-field-error]').forEach((target) => showError(target, ''));

  try {
    if (form.dataset.action === 'create') {
      const parentId = form.dataset.parentId ? Number(form.dataset.parentId) : null;
      await api.post(BASE, { parentId, ...formBody(form) });
    } else {
      await api.put(`${BASE}/${form.dataset.id}`, formBody(form));
    }

    closeDrawer();
    window.location.reload();
  } catch (error) {
    showFieldErrors(form, error);
  }
});

function init() {
  const root = el('[data-role="support-classifications-page"]');
  if (!root) {
    return;
  }

  root.addEventListener('click', (event) => {
    const button = event.target.closest('[data-classification-action]');
    if (!button) {
      return;
    }

    switch (button.dataset.classificationAction) {
      case 'add': {
        const parentId = button.dataset.parentId;
        const query = parentId ? `?parentId=${encodeURIComponent(parentId)}` : '';
        openDrawer(parentId ? 'Add classification item' : 'Add level-1 item', `${BASE}/AddDrawer${query}`);
        break;
      }
      case 'edit':
        openDrawer(`Edit — ${button.dataset.label}`, `${BASE}/${button.dataset.id}/EditDrawer`);
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

/**
 * Packages & contract durations screen (Controllers/CatalogueController.cs): add, edit, activate
 * or deactivate a package or a contract duration. Tables are rendered server-side, so every
 * successful write reloads the page. Validation and authorisation are the server's; this script
 * only shows what it returns.
 */
import { api, ApiError } from '../api-client.js';
import { openDrawer, closeDrawer, drawerBody } from '../drawer.js';

const BASE = '/ActivationRequests/Catalogue';

const ENTITIES = {
  package: {
    path: 'Packages',
    noun: 'package',
    addTitle: 'Add package',
    deactivateNote: 'It will no longer be offered on the New activation request form; existing requests and package offers are not affected.',
    body: (form, action) => {
      const tier = Number(form.querySelector('[name=tier]').value);
      const name = form.querySelector('[name=name]').value.trim();
      return action === 'create'
        ? { code: form.querySelector('[name=code]').value.trim().toUpperCase(), name, tier }
        : { name, tier };
    }
  },
  duration: {
    path: 'Durations',
    noun: 'contract duration',
    addTitle: 'Add contract duration',
    deactivateNote: 'It will no longer be offered on the New activation request form; existing requests and package offers are not affected.',
    body: (form, action) => {
      const label = form.querySelector('[name=label]').value.trim() || null;
      return action === 'create'
        ? { months: Number(form.querySelector('[name=months]').value), label }
        : { label };
    }
  }
};

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

function itemPath(entity, key) {
  return `${BASE}/${ENTITIES[entity].path}/${encodeURIComponent(key)}`;
}

async function setActive(button, isActive) {
  const entity = ENTITIES[button.dataset.entity];
  const message = isActive
    ? `Activate ${entity.noun} ${button.dataset.label}?`
    : `Deactivate ${entity.noun} ${button.dataset.label}? ${entity.deactivateNote}`;

  if (!(await confirmAction(message))) {
    return;
  }

  try {
    await api.patch(`${itemPath(button.dataset.entity, button.dataset.key)}/status`, { isActive });
    window.location.reload();
  } catch (error) {
    showError(el('#catalogue-error'), error instanceof ApiError ? error.message : 'Something went wrong. Please try again.');
  }
}

drawerBody.addEventListener('submit', async (event) => {
  const form = event.target;
  if (!(form instanceof HTMLFormElement) || !ENTITIES[form.dataset.entity]) {
    return;
  }
  event.preventDefault();
  form.querySelectorAll('[data-field-error]').forEach((target) => showError(target, ''));

  const entity = ENTITIES[form.dataset.entity];
  const action = form.dataset.action;

  try {
    if (action === 'create') {
      await api.post(`${BASE}/${entity.path}`, entity.body(form, action));
    } else if (action === 'update') {
      await api.put(itemPath(form.dataset.entity, form.dataset.key), entity.body(form, action));
    }

    closeDrawer();
    window.location.reload();
  } catch (error) {
    showFieldErrors(form, error);
  }
});

function init() {
  const root = el('[data-role="catalogue-admin-page"]');
  if (!root) {
    return;
  }

  root.addEventListener('click', (event) => {
    const button = event.target.closest('[data-catalogue-action]');
    if (!button || !ENTITIES[button.dataset.entity]) {
      return;
    }

    const entity = ENTITIES[button.dataset.entity];

    switch (button.dataset.catalogueAction) {
      case 'add':
        openDrawer(entity.addTitle, `${BASE}/${entity.path}/AddDrawer`);
        break;
      case 'edit':
        openDrawer(`Edit ${entity.noun} — ${button.dataset.label}`, `${itemPath(button.dataset.entity, button.dataset.key)}/EditDrawer`);
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

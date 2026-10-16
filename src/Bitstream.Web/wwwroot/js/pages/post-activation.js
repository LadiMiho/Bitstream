/**
 * Post-Activation Support (Controllers/PostActivationController.cs): the request list, the New
 * request drawer — phone number (with a filter for long lists), three linked classification
 * dropdowns, note — and the View drawer. Validation and authorisation are the server's; this
 * script only fills the dropdowns and shows what the server returns.
 */
import { api, ApiError, reportClientError } from '../api-client.js';
import { openDrawer, closeDrawer, drawerBody } from '../drawer.js';

const BASE = '/PostActivation';

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
    reportClientError('post-activation', error);
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

function fillSelect(select, items, placeholder, label) {
  select.replaceChildren(new Option(placeholder, ''));
  for (const item of items) {
    select.add(new Option(label(item), String(item.id)));
  }
  select.disabled = items.length === 0;
}

/** Level 1 → 2 → 3: changing one level refills the next and clears everything below it. */
function initClassifications(form) {
  const source = form.querySelector('[data-role="classification-tree"]');
  const tree = source ? JSON.parse(source.textContent || '[]') : [];
  const [level1, level2, level3] = [1, 2, 3].map((level) => form.querySelector(`select[data-level="${level}"]`));
  const byId = (items, id) => items.find((item) => String(item.id) === id);

  fillSelect(level1, tree, 'Select a request type', (item) => item.name);
  fillSelect(level2, [], 'Select a request type first', (item) => item.name);
  fillSelect(level3, [], 'Select a category first', (item) => item.name);

  level1.addEventListener('change', () => {
    const selected = byId(tree, level1.value);
    fillSelect(level2, selected ? selected.children : [], selected ? 'Select a category' : 'Select a request type first', (item) => item.name);
    fillSelect(level3, [], 'Select a category first', (item) => item.name);
  });

  level2.addEventListener('change', () => {
    const parent = byId(tree, level1.value);
    const selected = parent ? byId(parent.children, level2.value) : null;
    fillSelect(level3, selected ? selected.children : [], selected ? 'Select the detail' : 'Select a category first', (item) => `${item.name} · ${item.code}`);
  });
}

/** Narrows a long phone-number list as the user types; the selected option always stays. */
function initPhoneFilter(form) {
  const filter = form.querySelector('[data-role="phone-filter"]');
  const select = form.querySelector('select[name="phonenumber"]');
  if (!filter || !select) {
    return;
  }

  filter.addEventListener('input', () => {
    const term = filter.value.trim().toLowerCase();
    let firstMatch = null;
    for (const option of select.options) {
      if (!option.value) {
        continue;
      }
      const match = !term || option.value.toLowerCase().includes(term);
      option.hidden = !match && option.value !== select.value;
      if (match && !firstMatch) {
        firstMatch = option;
      }
    }
    if (term && !select.value && firstMatch) {
      select.value = firstMatch.value;
    }
  });
}

function initNoteCounter(form) {
  const note = form.querySelector('[data-role="note"]');
  const count = form.querySelector('[data-role="note-count"]');
  if (note && count) {
    note.addEventListener('input', () => {
      count.textContent = String(note.value.length);
    });
  }
}

function initForm() {
  const form = drawerBody.querySelector('[data-role="post-activation-form"]');
  if (!form || form.dataset.initialised) {
    return;
  }
  form.dataset.initialised = 'true';
  initClassifications(form);
  initPhoneFilter(form);
  initNoteCounter(form);
}

// The drawer's content arrives asynchronously; set the form up as soon as it is inserted.
new MutationObserver(initForm).observe(drawerBody, { childList: true, subtree: true });

drawerBody.addEventListener('submit', async (event) => {
  const form = event.target;
  if (!(form instanceof HTMLFormElement) || form.dataset.role !== 'post-activation-form') {
    return;
  }
  event.preventDefault();
  form.querySelectorAll('[data-field-error]').forEach((target) => showError(target, ''));

  const classificationId = Number(form.querySelector('select[data-level="3"]').value || 0);
  const body = {
    phonenumber: (form.querySelector('[name="phonenumber"]').value || '').trim(),
    classificationId,
    note: form.querySelector('[name="note"]').value.trim() || null
  };

  const submit = document.querySelector('button[form="post-activation-form"]');
  if (submit) {
    submit.disabled = true;
  }

  try {
    await api.post(BASE, body);
    closeDrawer();
    window.location.reload();
  } catch (error) {
    showFieldErrors(form, error);
  } finally {
    if (submit) {
      submit.disabled = false;
    }
  }
});

function init() {
  const root = document.querySelector('[data-role="post-activation-page"]');
  if (!root) {
    return;
  }

  const handle = (target) => {
    const trigger = target.closest('[data-post-activation-action]');
    if (!trigger) {
      return;
    }
    if (trigger.dataset.postActivationAction === 'new') {
      openDrawer('New post-activation request', `${BASE}/NewDrawer`);
    } else if (trigger.dataset.postActivationAction === 'view') {
      const publicId = trigger.dataset.publicId;
      openDrawer(`Request ${publicId}`, `${BASE}/Requests/${encodeURIComponent(publicId)}/ViewDrawer`);
    }
  };

  root.addEventListener('click', (event) => handle(event.target));
  root.addEventListener('keydown', (event) => {
    if (event.key === 'Enter' && event.target.matches('tr[data-post-activation-action]')) {
      handle(event.target);
    }
  });
}

init();

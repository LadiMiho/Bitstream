/**
 * User administration behaviour: search/browse grid, plus add/edit/view/change-password
 * (opened in a drawer, its form fetched from Controllers/UsersController.cs) and
 * delete/lock/unlock (gated by a standard confirmation popup). Every write is a direct call to
 * the existing /api/v1/users endpoints (Bitstream.Web/Endpoints/AdministrationEndpoints.cs) —
 * this script never validates or authorises anything itself, it only renders what the API and
 * the drawer partials return.
 */
import { api, ApiError } from '../api-client.js';

const PAGE_SIZE = 20;

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

let currentSkip = 0;
let currentSearch = '';
let currentTotalCount = 0;

// --- Drawer (add / edit / view / change password) --------------------------------------
const drawer = el('#drawer');
const drawerBackdrop = el('#drawer-backdrop');
const drawerTitle = el('#drawer-title');
const drawerBody = el('#drawer-body');

async function openDrawer(title, url) {
  drawerTitle.textContent = title;
  drawerBody.innerHTML = '';
  drawer.hidden = false;
  drawerBackdrop.hidden = false;
  drawer.setAttribute('aria-hidden', 'false');

  try {
    const response = await fetch(url, { credentials: 'same-origin' });

    if (!response.ok) {
      drawerBody.innerHTML = '<p class="text-sm text-ink-muted">This record is not available.</p>';
      return;
    }

    drawerBody.innerHTML = await response.text();
    drawerBody.querySelector('input, select, textarea')?.focus();
  } catch {
    drawerBody.innerHTML = '<p class="text-sm text-ink-muted">Something went wrong loading this form.</p>';
  }
}

function closeDrawer() {
  drawer.hidden = true;
  drawerBackdrop.hidden = true;
  drawer.setAttribute('aria-hidden', 'true');
  drawerBody.innerHTML = '';
}

// --- Confirmation popup (delete / lock / unlock) ----------------------------------------
const confirmModal = el('#confirm-modal');
const confirmBackdrop = el('#confirm-backdrop');
const confirmMessage = el('#confirm-message');
const confirmOk = el('#confirm-ok');
const confirmCancel = el('#confirm-cancel');

function confirmAction(message) {
  confirmMessage.textContent = message;
  confirmModal.hidden = false;
  confirmBackdrop.hidden = false;

  return new Promise((resolve) => {
    const cleanup = (result) => {
      confirmModal.hidden = true;
      confirmBackdrop.hidden = true;
      confirmOk.removeEventListener('click', onOk);
      confirmCancel.removeEventListener('click', onCancel);
      confirmBackdrop.removeEventListener('click', onCancel);
      resolve(result);
    };
    const onOk = () => cleanup(true);
    const onCancel = () => cleanup(false);

    confirmOk.addEventListener('click', onOk);
    confirmCancel.addEventListener('click', onCancel);
    confirmBackdrop.addEventListener('click', onCancel);
  });
}

// --- Grid ------------------------------------------------------------------------------
function actionButton(label, handler) {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'mr-3 text-sm text-brand-600 underline last:mr-0';
  button.textContent = label;
  button.addEventListener('click', handler);
  return button;
}

function renderResults(items) {
  const root = el('[data-role="user-admin-page"]');
  const canEdit = root.dataset.canEdit === 'true';
  const canLock = root.dataset.canLock === 'true';

  const body = el('#user-search-results');
  body.replaceChildren();

  for (const user of items) {
    const row = document.createElement('tr');

    for (const value of [user.fullName, user.email, user.role, user.status]) {
      const cell = document.createElement('td');
      cell.className = 'table-cell';
      cell.textContent = value;
      row.appendChild(cell);
    }

    const actionsCell = document.createElement('td');
    actionsCell.className = 'table-cell whitespace-nowrap';

    actionsCell.appendChild(
      actionButton('View', () => openDrawer(`${user.fullName} — details`, `/AccessManagement/Users/${user.userId}/ViewDrawer`))
    );

    if (canEdit) {
      actionsCell.appendChild(
        actionButton('Edit', () => openDrawer(`Edit ${user.fullName}`, `/AccessManagement/Users/${user.userId}/EditDrawer`))
      );
      actionsCell.appendChild(
        actionButton('Change password', () => openDrawer(`Change password — ${user.fullName}`, `/AccessManagement/Users/${user.userId}/ChangePasswordDrawer`))
      );
    }

    if (canLock) {
      const nextStatus = user.status === 'Locked' ? 'Active' : 'Locked';
      actionsCell.appendChild(
        actionButton(nextStatus === 'Locked' ? 'Lock' : 'Unlock', () => confirmAndSetStatus(user, nextStatus))
      );
      actionsCell.appendChild(actionButton('Delete', () => confirmAndDelete(user)));
    }

    row.appendChild(actionsCell);
    body.appendChild(row);
  }
}

async function confirmAndSetStatus(user, status) {
  const verb = status === 'Locked' ? 'Lock' : 'Unlock';
  const confirmed = await confirmAction(`${verb} ${user.fullName}?`);

  if (!confirmed) {
    return;
  }

  try {
    await api.patch(`/api/v1/users/${user.userId}/status`, { status });
    await search(currentSkip, currentSearch);
  } catch (error) {
    showError(el('#user-search-error'), describeError(error));
  }
}

async function confirmAndDelete(user) {
  const confirmed = await confirmAction(`Delete ${user.fullName}? Their sessions are revoked immediately and they will no longer be able to sign in.`);

  if (!confirmed) {
    return;
  }

  try {
    await api.delete(`/api/v1/users/${user.userId}`);
    await search(currentSkip, currentSearch);
  } catch (error) {
    showError(el('#user-search-error'), describeError(error));
  }
}

async function search(skip, searchTerm) {
  const searchError = el('#user-search-error');
  showError(searchError, '');

  try {
    const params = new URLSearchParams({ skip: String(skip), take: String(PAGE_SIZE) });
    if (searchTerm) {
      params.set('search', searchTerm);
    }

    const result = await api.get(`/api/v1/users?${params}`);
    currentSkip = skip;
    currentSearch = searchTerm;
    currentTotalCount = result.totalCount;

    renderResults(result.items);
    el('#user-search-empty').hidden = result.items.length > 0;

    const shown = result.items.length === 0 ? 0 : skip + 1;
    const shownTo = skip + result.items.length;
    el('#user-search-summary').textContent =
      result.totalCount === 0 ? 'No results' : `${shown}–${shownTo} of ${result.totalCount}`;
    el('#user-search-prev').disabled = skip === 0;
    el('#user-search-next').disabled = skip + PAGE_SIZE >= result.totalCount;
  } catch (error) {
    showError(searchError, describeError(error));
  }
}

// --- Drawer form submission (delegated: forms are injected dynamically) ----------------
function ispIdOrNull(value) {
  const trimmed = value.trim();
  return trimmed === '' ? null : Number(trimmed);
}

drawerBody.addEventListener('submit', async (event) => {
  const form = event.target;

  if (!(form instanceof HTMLFormElement)) {
    return;
  }

  event.preventDefault();

  const action = form.dataset.action;
  const errorTarget = form.querySelector('.field-error');
  showError(errorTarget, '');

  try {
    if (action === 'create') {
      await api.post('/api/v1/users', {
        ispId: ispIdOrNull(form.querySelector('[name=ispId]').value),
        fullName: form.querySelector('[name=fullName]').value.trim(),
        email: form.querySelector('[name=email]').value.trim(),
        mobile: form.querySelector('[name=mobile]').value.trim(),
        roleName: form.querySelector('[name=roleName]').value,
        initialPassword: form.querySelector('[name=initialPassword]').value
      });
    } else if (action === 'update') {
      await api.put(`/api/v1/users/${form.dataset.userId}`, {
        ispId: ispIdOrNull(form.querySelector('[name=ispId]').value),
        fullName: form.querySelector('[name=fullName]').value.trim(),
        email: form.querySelector('[name=email]').value.trim(),
        mobile: form.querySelector('[name=mobile]').value.trim(),
        roleName: form.querySelector('[name=roleName]').value
      });
    } else if (action === 'change-password') {
      await api.post(`/api/v1/users/${form.dataset.userId}/password`, {
        newPassword: form.querySelector('[name=newPassword]').value
      });
    }

    closeDrawer();
    await search(currentSkip, currentSearch);
  } catch (error) {
    showError(errorTarget, describeError(error));
  }
});

function init() {
  const root = el('[data-role="user-admin-page"]');
  if (!root) {
    return;
  }

  el('#user-add-button')?.addEventListener('click', () => openDrawer('Add user', '/AccessManagement/Users/AddDrawer'));

  el('#drawer-close').addEventListener('click', closeDrawer);
  drawerBackdrop.addEventListener('click', closeDrawer);
  drawerBody.addEventListener('click', (event) => {
    if (event.target.closest('[data-drawer-cancel]')) {
      closeDrawer();
    }
  });

  el('#user-search-form').addEventListener('submit', (event) => {
    event.preventDefault();
    search(0, el('#user-search-query').value.trim());
  });

  el('#user-search-prev').addEventListener('click', () => search(Math.max(0, currentSkip - PAGE_SIZE), currentSearch));
  el('#user-search-next').addEventListener('click', () => {
    if (currentSkip + PAGE_SIZE < currentTotalCount) {
      search(currentSkip + PAGE_SIZE, currentSearch);
    }
  });

  search(0, '');
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', init, { once: true });
} else {
  init();
}

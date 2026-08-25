/**
 * User administration behaviour: search/filter/browse grid, plus add/edit/view/change-password
 * (opened in the shared drawer, ../drawer.js, its form fetched from Controllers/UsersController.cs)
 * and delete/lock/unlock (gated by a standard confirmation popup). Every write is a direct call to
 * the existing /api/v1/users endpoints (Bitstream.Web/Endpoints/AdministrationEndpoints.cs) —
 * this script never validates or authorises anything itself, it only renders what the API and
 * the drawer partials return.
 */
import { api, ApiError } from '../api-client.js';
import { openDrawer, closeDrawer, drawerBody } from '../drawer.js';

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

/**
 * TR-NFR-12: shows each server-reported violation next to the field it concerns — a
 * `[data-field-error="fieldName"]` element next to that field, matching the key the API used
 * (AdministrationEndpoints.ValidationProblem) — falling back to the form's general error banner
 * for anything that isn't (or can't be) tied to one field, e.g. a network failure.
 */
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

/** For contexts with no per-field targets to show against (the search bar, a grid-row action). */
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
let currentRole = '';
let currentStatus = '';
let currentPageSize = 20;
let currentTotalCount = 0;

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

// --- Icons (inline, no icon font/library) -----------------------------------------------
const ICONS = {
  view: '<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.6"><path d="M1.5 10S4.5 4 10 4s8.5 6 8.5 6-3 6-8.5 6-8.5-6-8.5-6Z" stroke-linejoin="round"/><circle cx="10" cy="10" r="2.5"/></svg>',
  edit: '<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round"><path d="M13.5 3.5 16.5 6.5 7 16H4v-3L13.5 3.5Z"/></svg>',
  key: '<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round"><circle cx="7" cy="13" r="3.5"/><path d="M9.5 10.5 16 4M13.5 6.5 16 4M16 4l1.5 1.5"/></svg>',
  lock: '<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round"><rect x="4.5" y="9" width="11" height="7.5" rx="1.2"/><path d="M6.5 9V6.5a3.5 3.5 0 0 1 7 0V9"/></svg>',
  unlock: '<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round"><rect x="4.5" y="9" width="11" height="7.5" rx="1.2"/><path d="M6.5 9V6.5a3.5 3.5 0 0 1 6.6-1.5"/></svg>',
  delete: '<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M4 5.5h12M8 5.5V4a1 1 0 0 1 1-1h2a1 1 0 0 1 1 1v1.5M6 5.5 6.7 16a1 1 0 0 0 1 .9h4.6a1 1 0 0 0 1-.9l.7-10.5"/></svg>',
  kebab: '<svg viewBox="0 0 20 20" fill="currentColor" class="h-4 w-4"><circle cx="10" cy="4" r="1.5"/><circle cx="10" cy="10" r="1.5"/><circle cx="10" cy="16" r="1.5"/></svg>'
};

function menuItem(label, icon, handler, { danger = false } = {}) {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = danger ? 'menu-item menu-item-danger' : 'menu-item';
  button.innerHTML = `${icon}<span>${label}</span>`;
  button.addEventListener('click', (event) => {
    event.stopPropagation();
    closeAllMenus();
    handler();
  });
  return button;
}

let openMenuTrigger = null;

function closeAllMenus() {
  document.querySelectorAll('.menu-panel').forEach((panel) => panel.remove());
  openMenuTrigger = null;
}

/**
 * Row-actions dropdowns are appended to <body>, not the trigger's table cell — the grid sits in
 * an overflow-x-auto container, and a menu-panel nested inside it would grow that container's
 * scrollable content box, forcing a vertical scrollbar onto the whole grid card whenever a menu
 * near the bottom of the (short, unscrolled) table opened. Fixed positioning keyed off the
 * trigger's own viewport rect avoids that entirely and still tracks the row correctly.
 */
function openRowMenu(trigger, menu) {
  menu.style.position = 'fixed';
  menu.style.visibility = 'hidden';
  document.body.appendChild(menu);

  const triggerRect = trigger.getBoundingClientRect();
  const menuRect = menu.getBoundingClientRect();
  const left = Math.min(triggerRect.right - menuRect.width, window.innerWidth - menuRect.width - 8);
  const top = triggerRect.bottom + menuRect.height <= window.innerHeight
    ? triggerRect.bottom + 4
    : triggerRect.top - menuRect.height - 4;

  menu.style.left = `${Math.max(8, left)}px`;
  menu.style.top = `${Math.max(8, top)}px`;
  menu.style.visibility = '';

  openMenuTrigger = trigger;
}

document.addEventListener('click', closeAllMenus);
window.addEventListener('scroll', closeAllMenus, true);
window.addEventListener('resize', closeAllMenus);

// --- Grid ------------------------------------------------------------------------------
function initials(fullName) {
  const parts = fullName.trim().split(/\s+/).filter(Boolean);
  const first = parts[0]?.[0] ?? '';
  const last = parts.length > 1 ? parts[parts.length - 1][0] : '';
  return (first + last).toUpperCase();
}

function statusPill(status) {
  const tone = status === 'Locked' ? 'text-ink-muted bg-ink-muted/10' : 'text-state-done bg-state-done/15';
  const span = document.createElement('span');
  span.className = `status-pill status-dot ${tone}`;
  span.textContent = status;
  return span;
}

function renderResults(items) {
  const root = el('[data-role="user-admin-page"]');
  const canEdit = root.dataset.canEdit === 'true';
  const canLock = root.dataset.canLock === 'true';

  const body = el('#user-search-results');
  body.replaceChildren();

  for (const user of items) {
    const row = document.createElement('tr');
    row.className = 'border-t border-line';

    const nameCell = document.createElement('td');
    nameCell.className = 'table-cell';
    nameCell.innerHTML = `
      <div class="flex items-center gap-3">
        <span class="avatar-circle" aria-hidden="true">${initials(user.fullName)}</span>
        <div>
          <div class="font-medium text-ink">${user.fullName}</div>
          <div class="text-xs text-ink-muted">${user.email}</div>
        </div>
      </div>`;
    row.appendChild(nameCell);

    const roleCell = document.createElement('td');
    roleCell.className = 'table-cell';
    const roleBadge = document.createElement('span');
    roleBadge.className = 'badge';
    roleBadge.textContent = user.role;
    roleCell.appendChild(roleBadge);
    row.appendChild(roleCell);

    const statusCell = document.createElement('td');
    statusCell.className = 'table-cell';
    statusCell.appendChild(statusPill(user.status));
    row.appendChild(statusCell);

    const lastLoginCell = document.createElement('td');
    lastLoginCell.className = 'table-cell text-ink-muted';
    lastLoginCell.textContent = user.lastLoginAt ? new Date(user.lastLoginAt).toLocaleDateString() : 'Never';
    row.appendChild(lastLoginCell);

    const actionsCell = document.createElement('td');
    actionsCell.className = 'table-cell w-10 text-right';

    const trigger = document.createElement('button');
    trigger.type = 'button';
    trigger.className = 'icon-button';
    trigger.setAttribute('aria-label', `Actions for ${user.fullName}`);
    trigger.innerHTML = ICONS.kebab;
    trigger.addEventListener('click', (event) => {
      event.stopPropagation();
      const alreadyOpen = openMenuTrigger === trigger;
      closeAllMenus();
      if (alreadyOpen) {
        return;
      }

      const menu = document.createElement('div');
      menu.className = 'menu-panel';
      menu.appendChild(menuItem('View', ICONS.view, () =>
        openDrawer(`${user.fullName} — details`, `/AccessManagement/Users/${user.userId}/ViewDrawer`)));

      if (canEdit) {
        menu.appendChild(menuItem('Edit', ICONS.edit, () =>
          openDrawer(`Edit ${user.fullName}`, `/AccessManagement/Users/${user.userId}/EditDrawer`)));
        menu.appendChild(menuItem('New password', ICONS.key, () =>
          openDrawer(`Change password — ${user.fullName}`, `/AccessManagement/Users/${user.userId}/ChangePasswordDrawer`)));
      }

      if (canLock) {
        const nextStatus = user.status === 'Locked' ? 'Active' : 'Locked';
        menu.appendChild(menuItem(
          nextStatus === 'Locked' ? 'Lock' : 'Unlock',
          nextStatus === 'Locked' ? ICONS.lock : ICONS.unlock,
          () => confirmAndSetStatus(user, nextStatus)
        ));
        menu.appendChild(menuItem('Delete', ICONS.delete, () => confirmAndDelete(user), { danger: true }));
      }

      openRowMenu(trigger, menu);
    });

    actionsCell.appendChild(trigger);
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
    await search();
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
    await search();
  } catch (error) {
    showError(el('#user-search-error'), describeError(error));
  }
}

async function search() {
  const searchError = el('#user-search-error');
  showError(searchError, '');

  try {
    const params = new URLSearchParams({ skip: String(currentSkip), take: String(currentPageSize) });
    if (currentSearch) params.set('search', currentSearch);
    if (currentRole) params.set('role', currentRole);
    if (currentStatus) params.set('status', currentStatus);

    const result = await api.get(`/api/v1/users?${params}`);
    currentTotalCount = result.totalCount;

    renderResults(result.items);
    el('#user-search-empty').hidden = result.items.length > 0;

    const shown = result.items.length === 0 ? 0 : currentSkip + 1;
    const shownTo = currentSkip + result.items.length;
    el('#user-search-summary').textContent =
      result.totalCount === 0 ? 'No results' : `Showing ${shown}–${shownTo} of ${result.totalCount}`;
    el('#user-search-prev').disabled = currentSkip === 0;
    el('#user-search-next').disabled = currentSkip + currentPageSize >= result.totalCount;
    el('#user-page-number').textContent = String(Math.floor(currentSkip / currentPageSize) + 1);
  } catch (error) {
    showError(searchError, describeError(error));
  }
}

// --- Filters popover ---------------------------------------------------------------------
function renderFilterChips() {
  const container = el('#user-filter-chips');
  container.replaceChildren();

  const active = [
    currentRole && { key: 'role', label: `Role: ${currentRole}` },
    currentStatus && { key: 'status', label: `Status: ${currentStatus}` }
  ].filter(Boolean);

  const countBadge = el('#user-filters-count');
  countBadge.hidden = active.length === 0;
  countBadge.textContent = String(active.length);

  for (const filter of active) {
    const chip = document.createElement('span');
    chip.className = 'filter-chip';
    chip.innerHTML = `<span>${filter.label}</span>`;
    const clear = document.createElement('button');
    clear.type = 'button';
    clear.setAttribute('aria-label', `Clear ${filter.label}`);
    clear.textContent = '×';
    clear.addEventListener('click', () => {
      if (filter.key === 'role') {
        currentRole = '';
        el('#user-filter-role').value = '';
      } else {
        currentStatus = '';
        el('#user-filter-status').value = '';
      }
      currentSkip = 0;
      renderFilterChips();
      search();
    });
    chip.appendChild(clear);
    container.appendChild(chip);
  }

  if (active.length > 0) {
    const clearAll = document.createElement('button');
    clearAll.type = 'button';
    clearAll.className = 'text-sm text-brand-600 underline';
    clearAll.textContent = 'Clear';
    clearAll.addEventListener('click', () => {
      currentRole = '';
      currentStatus = '';
      el('#user-filter-role').value = '';
      el('#user-filter-status').value = '';
      currentSkip = 0;
      renderFilterChips();
      search();
    });
    container.appendChild(clearAll);
  }
}

function initFilters() {
  const button = el('#user-filters-button');
  const panel = el('#user-filters-panel');

  button.addEventListener('click', (event) => {
    event.stopPropagation();
    const isHidden = panel.hidden;
    panel.hidden = !isHidden;
    button.setAttribute('aria-expanded', String(isHidden));
  });

  panel.addEventListener('click', (event) => event.stopPropagation());

  document.addEventListener('click', () => {
    panel.hidden = true;
    button.setAttribute('aria-expanded', 'false');
  });

  el('#user-filters-form').addEventListener('submit', (event) => {
    event.preventDefault();
    currentRole = el('#user-filter-role').value;
    currentStatus = el('#user-filter-status').value;
    currentSkip = 0;
    panel.hidden = true;
    button.setAttribute('aria-expanded', 'false');
    renderFilterChips();
    search();
  });

  el('#user-filters-reset').addEventListener('click', () => {
    el('#user-filter-role').value = '';
    el('#user-filter-status').value = '';
  });
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
  form.querySelectorAll('[data-field-error]').forEach((target) => showError(target, ''));

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
    await search();
  } catch (error) {
    showFieldErrors(form, error);
  }
});

function init() {
  const root = el('[data-role="user-admin-page"]');
  if (!root) {
    return;
  }

  el('#user-add-button')?.addEventListener('click', () => openDrawer('Add user', '/AccessManagement/Users/AddDrawer'));

  el('#user-search-form').addEventListener('submit', (event) => {
    event.preventDefault();
    currentSearch = el('#user-search-query').value.trim();
    currentSkip = 0;
    search();
  });

  el('#user-page-size').addEventListener('change', (event) => {
    currentPageSize = Number(event.target.value);
    currentSkip = 0;
    search();
  });

  el('#user-search-prev').addEventListener('click', () => {
    currentSkip = Math.max(0, currentSkip - currentPageSize);
    search();
  });
  el('#user-search-next').addEventListener('click', () => {
    if (currentSkip + currentPageSize < currentTotalCount) {
      currentSkip += currentPageSize;
      search();
    }
  });

  initFilters();
  search();
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', init, { once: true });
} else {
  init();
}

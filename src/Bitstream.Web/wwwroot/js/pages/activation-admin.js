/**
 * Activation request administration: search/filter/browse grid, plus a drawer form for
 * submitting a new request (its form fetched from Controllers/ActivationRequestsController.cs)
 * and, for an eligible request, recording the GIS verification outcome — mirrors
 * user-admin.js/isp-admin.js's pattern. Every write is a direct call back to that same
 * controller's JSON actions — this script never validates or authorises anything itself, it only
 * renders what the server and the drawer partials return.
 */
import { api, ApiError } from '../api-client.js';
import { openDrawer, closeDrawer, drawerBody } from '../drawer.js';
import { presentStatus } from '../status-presentation.js';

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
 * TR-NFR-12: shows each server-reported violation next to the field it concerns when the server
 * keyed one — falling back to the form's general error banner for anything that isn't (or can't
 * be) tied to one field, e.g. a network failure. ActivationRequestsController currently reports
 * every validation failure under "request", so in practice this always lands on the banner.
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
let currentStatus = '';
let currentPageSize = 20;
let currentTotalCount = 0;

// --- Icons (inline, no icon font/library) -----------------------------------------------
const ICONS = {
  view: '<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.6"><path d="M1.5 10S4.5 4 10 4s8.5 6 8.5 6-3 6-8.5 6-8.5-6-8.5-6Z" stroke-linejoin="round"/><circle cx="10" cy="10" r="2.5"/></svg>',
  gis: '<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round" stroke-linecap="round"><path d="M10 17s6-5.5 6-9.5A6 6 0 1 0 4 7.5C4 11.5 10 17 10 17Z"/><circle cx="10" cy="7.5" r="2"/></svg>',
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
function initials(name) {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  const first = parts[0]?.[0] ?? '';
  const last = parts.length > 1 ? parts[parts.length - 1][0] : '';
  return (first + last).toUpperCase();
}

function statusPill(status) {
  const presented = presentStatus(status);
  const span = document.createElement('span');
  span.className = presented.className;
  span.textContent = presented.label;
  return span;
}

function renderResults(items) {
  const root = el('[data-role="activation-admin-page"]');
  const canRecordGis = root.dataset.canRecordGis === 'true';

  const body = el('#activation-search-results');
  body.replaceChildren();

  for (const request of items) {
    const row = document.createElement('tr');
    row.className = 'border-t border-line';

    const requestCell = document.createElement('td');
    requestCell.className = 'table-cell';
    requestCell.innerHTML = `
      <div class="font-medium text-ink">${request.publicId}</div>
      <div class="text-xs text-ink-muted">${request.packageCode}</div>`;
    row.appendChild(requestCell);

    const ispCell = document.createElement('td');
    ispCell.className = 'table-cell';
    ispCell.innerHTML = `
      <div class="flex items-center gap-3">
        <span class="avatar-circle" aria-hidden="true">${initials(request.ispName)}</span>
        <div>
          <div class="font-medium text-ink">${request.ispName}</div>
          <div class="text-xs text-ink-muted">#${request.ispId}</div>
        </div>
      </div>`;
    row.appendChild(ispCell);

    const statusCell = document.createElement('td');
    statusCell.className = 'table-cell';
    statusCell.appendChild(statusPill(request.status));
    row.appendChild(statusCell);

    const submittedCell = document.createElement('td');
    submittedCell.className = 'table-cell text-ink-muted';
    submittedCell.textContent = new Date(request.createdAt).toLocaleDateString();
    row.appendChild(submittedCell);

    const actionsCell = document.createElement('td');
    actionsCell.className = 'table-cell w-10 text-right';

    const trigger = document.createElement('button');
    trigger.type = 'button';
    trigger.className = 'icon-button';
    trigger.setAttribute('aria-label', `Actions for ${request.publicId}`);
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
        openDrawer(`${request.publicId} — details`, `/ActivationRequests/${encodeURIComponent(request.publicId)}/ViewDrawer`)));

      if (canRecordGis && request.status === 'AwaitingGisVerification') {
        menu.appendChild(menuItem('Record GIS outcome', ICONS.gis, () =>
          openDrawer(`GIS outcome — ${request.publicId}`, `/ActivationRequests/${encodeURIComponent(request.publicId)}/GisOutcomeDrawer`)));
      }

      openRowMenu(trigger, menu);
    });

    actionsCell.appendChild(trigger);
    row.appendChild(actionsCell);
    body.appendChild(row);
  }
}

async function search() {
  const searchError = el('#activation-search-error');
  showError(searchError, '');

  try {
    const params = new URLSearchParams({ skip: String(currentSkip), take: String(currentPageSize) });
    if (currentSearch) params.set('search', currentSearch);
    if (currentStatus) params.set('status', currentStatus);

    const result = await api.get(`/ActivationRequests/Search?${params}`);
    currentTotalCount = result.totalCount;

    renderResults(result.items);
    el('#activation-search-empty').hidden = result.items.length > 0;

    const shown = result.items.length === 0 ? 0 : currentSkip + 1;
    const shownTo = currentSkip + result.items.length;
    el('#activation-search-summary').textContent =
      result.totalCount === 0 ? 'No results' : `Showing ${shown}–${shownTo} of ${result.totalCount}`;
    el('#activation-search-prev').disabled = currentSkip === 0;
    el('#activation-search-next').disabled = currentSkip + currentPageSize >= result.totalCount;
    el('#activation-page-number').textContent = String(Math.floor(currentSkip / currentPageSize) + 1);
  } catch (error) {
    showError(searchError, describeError(error));
  }
}

// --- Filters popover ---------------------------------------------------------------------
function renderFilterChips() {
  const container = el('#activation-filter-chips');
  container.replaceChildren();

  const active = [currentStatus && { key: 'status', label: `Status: ${presentStatus(currentStatus).label}` }].filter(Boolean);

  const countBadge = el('#activation-filters-count');
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
      currentStatus = '';
      el('#activation-filter-status').value = '';
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
      currentStatus = '';
      el('#activation-filter-status').value = '';
      currentSkip = 0;
      renderFilterChips();
      search();
    });
    container.appendChild(clearAll);
  }
}

function initFilters() {
  const button = el('#activation-filters-button');
  const panel = el('#activation-filters-panel');

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

  el('#activation-filters-form').addEventListener('submit', (event) => {
    event.preventDefault();
    currentStatus = el('#activation-filter-status').value;
    currentSkip = 0;
    panel.hidden = true;
    button.setAttribute('aria-expanded', 'false');
    renderFilterChips();
    search();
  });

  el('#activation-filters-reset').addEventListener('click', () => {
    el('#activation-filter-status').value = '';
  });
}

// --- Drawer form submission (delegated: forms are injected dynamically) ----------------
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
      await api.post('/ActivationRequests', {
        ispId: Number(form.querySelector('[name=ispId]').value),
        packageCode: form.querySelector('[name=packageCode]').value,
        locationRaw: form.querySelector('[name=locationRaw]').value.trim(),
        classification: form.querySelector('[name=classification]').value,
        contractDurationMonths: Number(form.querySelector('[name=contractDurationMonths]').value),
        comments: form.querySelector('[name=comments]').value.trim() || null
      });
    } else if (action === 'gis-outcome') {
      const selected = form.querySelector('input[name="lineAvailable"]:checked');
      if (!selected) {
        showError(form.querySelector('[data-field-error="request"]'), 'Choose line exists or no line.');
        return;
      }

      const lineAvailable = selected.value === 'true';
      const reason = form.querySelector('[name=reason]').value.trim() || null;

      if (!lineAvailable && !reason) {
        showError(form.querySelector('[data-field-error="request"]'), 'A reason is required when recording no line (TR-ACT-13).');
        return;
      }

      await api.patch(`/ActivationRequests/${form.dataset.requestId}/gis-outcome`, { lineAvailable, reason });
    }

    closeDrawer();
    await search();
  } catch (error) {
    showFieldErrors(form, error);
  }
});

function init() {
  const root = el('[data-role="activation-admin-page"]');
  if (!root) {
    return;
  }

  el('#activation-add-button')?.addEventListener('click', () => openDrawer('New activation request', '/ActivationRequests/AddDrawer'));

  el('#activation-search-form').addEventListener('submit', (event) => {
    event.preventDefault();
    currentSearch = el('#activation-search-query').value.trim();
    currentSkip = 0;
    search();
  });

  el('#activation-page-size').addEventListener('change', (event) => {
    currentPageSize = Number(event.target.value);
    currentSkip = 0;
    search();
  });

  el('#activation-search-prev').addEventListener('click', () => {
    currentSkip = Math.max(0, currentSkip - currentPageSize);
    search();
  });
  el('#activation-search-next').addEventListener('click', () => {
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

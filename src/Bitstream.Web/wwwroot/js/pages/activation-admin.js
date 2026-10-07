/**
 * Activation request administration: search/filter/browse grid, plus a drawer form for
 * submitting a new request (its form fetched from Controllers/ActivationRequestsController.cs)
 * and, for an eligible request, recording the GIS verification outcome, the operator's
 * confirmation that the activated line works, and the service desk's final decision — mirrors
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
 * TR-NFR-12: shows each server-reported violation next to the field it concerns — a
 * `[data-field-error="fieldName"]` element next to that field, matching the key the server used
 * (ActivationRequestsController.ValidationProblemFor) — falling back to the form's general error
 * banner for anything that isn't (or can't be) tied to one field, e.g. a network failure.
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
  confirm: '<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round" stroke-linecap="round"><circle cx="10" cy="10" r="7.5"/><path d="m6.5 10 2.5 2.5 4.5-5"/></svg>',
  serviceDesk: '<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round" stroke-linecap="round"><path d="M4 11V9a6 6 0 0 1 12 0v2"/><rect x="2.5" y="11" width="3.5" height="5" rx="1"/><rect x="14" y="11" width="3.5" height="5" rx="1"/></svg>',
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
  const canConfirm = root.dataset.canConfirm === 'true';
  const canServiceDeskDecide = root.dataset.canServiceDeskDecide === 'true';

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

      if (canConfirm && request.status === 'AwaitingOperatorConfirmation') {
        menu.appendChild(menuItem('Confirm activation', ICONS.confirm, () =>
          openDrawer(`Confirm activation — ${request.publicId}`, `/ActivationRequests/${encodeURIComponent(request.publicId)}/OperatorConfirmationDrawer`)));
      }

      if (canServiceDeskDecide && request.status === 'WaitingForServiceDesk') {
        menu.appendChild(menuItem('Service desk decision', ICONS.serviceDesk, () =>
          openDrawer(`Service desk decision — ${request.publicId}`, `/ActivationRequests/${encodeURIComponent(request.publicId)}/ServiceDeskDecisionDrawer`)));
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

// --- Contract duration filtering (Add drawer) -----------------------------------------
// A duration is offered for a package only when portal.PackageOffer has a code for that pair;
// each duration <option> lists those packages in data-packages. The server re-checks on submit.
function syncDurationOptions(form) {
  const packageSelect = form?.querySelector('[data-role="package-select"]');
  const durationSelect = form?.querySelector('[data-role="duration-select"]');
  if (!packageSelect || !durationSelect) {
    return;
  }

  const packageCode = packageSelect.value;
  let available = 0;

  for (const option of durationSelect.options) {
    if (option.value === '') {
      continue;
    }
    const offered = packageCode !== '' && (option.dataset.packages ?? '').split(' ').includes(packageCode);
    option.hidden = !offered;
    option.disabled = !offered;
    if (offered) {
      available += 1;
    }
  }

  if (durationSelect.selectedOptions[0]?.disabled) {
    durationSelect.value = '';
  }

  durationSelect.disabled = packageCode === '' || available === 0;
  const hint = form.querySelector('[data-role="duration-hint"]');
  if (hint) {
    hint.hidden = packageCode === '' || available > 0;
  }
}

drawerBody.addEventListener('change', (event) => {
  if (event.target.matches('[data-role="package-select"]')) {
    syncDurationOptions(event.target.form);
  }

  // Operator "No" makes the comment required; show that next to the label.
  if (event.target.matches('input[name="decision"]')) {
    const marker = event.target.form?.querySelector('[data-role="comment-required"]');
    if (marker && event.target.form.dataset.commentAlwaysRequired !== 'true') {
      marker.hidden = event.target.value !== 'false';
    }
  }
});

/** Reads a yes/no decision form (operator confirmation / service desk decision); null when nothing is selected. */
function readDecision(form, chooseMessage, commentRequiredMessage) {
  const selected = form.querySelector('input[name="decision"]:checked');
  if (!selected) {
    showError(form.querySelector('[data-field-error="decision"]'), chooseMessage);
    return null;
  }

  const decision = selected.value === 'true';
  const comment = form.querySelector('[name=comment]').value.trim() || null;
  const commentRequired = form.dataset.commentAlwaysRequired === 'true' || !decision;

  if (commentRequired && !comment) {
    showError(form.querySelector('[data-field-error="comment"]'), commentRequiredMessage);
    return null;
  }

  return { decision, comment };
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
    } else if (action === 'operator-confirmation') {
      const result = readDecision(form, 'Choose yes or no.', 'A comment is required when the line is not working.');
      if (!result) {
        return;
      }

      await api.patch(`/ActivationRequests/${form.dataset.requestId}/operator-confirmation`, { working: result.decision, comment: result.comment });
    } else if (action === 'service-desk-decision') {
      const result = readDecision(form, 'Choose success or fail.', 'A comment is required.');
      if (!result) {
        return;
      }

      await api.patch(`/ActivationRequests/${form.dataset.requestId}/service-desk-decision`, { success: result.decision, comment: result.comment });
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

  el('#activation-add-button')?.addEventListener('click', async () => {
    await openDrawer('New activation request', '/ActivationRequests/AddDrawer');
    syncDurationOptions(drawerBody.querySelector('#activation-add-form'));
  });

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

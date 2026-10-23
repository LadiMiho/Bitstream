/**
 * Activation request administration: search/filter/browse grid, plus a drawer form for
 * submitting a new request (its form fetched from Controllers/ActivationRequestsController.cs)
 * and, for an eligible request, recording the GIS verification outcome, the operator's
 * confirmation that the activated line works, and the service desk's final decision — mirrors
 * user-admin.js/isp-admin.js's pattern. Every write is a direct call back to that same
 * controller's JSON actions — this script never validates or authorises anything itself, it only
 * renders what the server and the drawer partials return.
 */
import { api, ApiError, reportClientError } from '../api-client.js';
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
    reportClientError('activation-requests', error);
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
    reportClientError('activation-requests', error);
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
// The Filters popup's applied values, keyed by the form field / query parameter name.
let currentFilters = {};
let currentPageSize = 20;
let currentTotalCount = 0;

// --- Icons (inline, no icon font/library) -----------------------------------------------
const ICONS = {
  view: '<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.6"><path d="M1.5 10S4.5 4 10 4s8.5 6 8.5 6-3 6-8.5 6-8.5-6-8.5-6Z" stroke-linejoin="round"/><circle cx="10" cy="10" r="2.5"/></svg>',
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
function statusPill(status) {
  const presented = presentStatus(status);
  const span = document.createElement('span');
  span.className = presented.className;
  span.textContent = presented.label;
  return span;
}

function cell(className, text) {
  const td = document.createElement('td');
  td.className = `table-cell ${className}`.trim();
  if (text !== undefined) {
    td.textContent = text;
  }
  return td;
}

function openView(publicId) {
  openDrawer(`${publicId} — details`, `/ActivationRequests/${encodeURIComponent(publicId)}/ViewDrawer`);
}

/**
 * The row's ⋮ menu: View, then the steps this user may take at this status. The GIS outcome is
 * not one of them — CRM reports it through the API (LINE_AVAILABLE / NO_LINE).
 */
function rowActions(request, permissions) {
  const id = encodeURIComponent(request.publicId);
  const actions = [['View', ICONS.view, `${request.publicId} — details`, `/ActivationRequests/${id}/ViewDrawer`]];

  if (permissions.canConfirm && request.status === 'AwaitingOperatorConfirmation') {
    actions.push(['Confirm activation', ICONS.confirm, `Confirm activation — ${request.publicId}`, `/ActivationRequests/${id}/OperatorConfirmationDrawer`]);
  }
  if (permissions.canServiceDeskDecide && request.status === 'WaitingForServiceDesk') {
    actions.push(['Service desk decision', ICONS.serviceDesk, `Service desk decision — ${request.publicId}`, `/ActivationRequests/${id}/ServiceDeskDecisionDrawer`]);
  }

  return actions;
}

function renderResults(items) {
  const root = el('[data-role="activation-admin-page"]');
  const showIsp = root.dataset.showIsp === 'true';
  const permissions = {
    canConfirm: root.dataset.canConfirm === 'true',
    canServiceDeskDecide: root.dataset.canServiceDeskDecide === 'true'
  };

  const body = el('#activation-search-results');
  body.replaceChildren();

  for (const request of items) {
    const row = document.createElement('tr');
    row.className = 'cursor-pointer border-t border-line hover:bg-surface-muted';
    row.tabIndex = 0;
    row.setAttribute('aria-label', `View ${request.publicId}`);
    row.addEventListener('click', () => openView(request.publicId));
    row.addEventListener('keydown', (event) => {
      if (event.key === 'Enter' && event.target === row) {
        openView(request.publicId);
      }
    });

    row.appendChild(cell('font-medium text-brand-700', request.publicId));

    if (showIsp) {
      row.appendChild(cell('', request.ispName));
    }

    const packageCell = cell('');
    const packageName = document.createElement('div');
    packageName.className = 'text-ink';
    packageName.textContent = request.packageName || request.packageCode;
    const duration = document.createElement('div');
    duration.className = 'text-xs text-ink-muted';
    duration.textContent = `${request.contractDurationMonths} months`;
    packageCell.append(packageName, duration);
    row.appendChild(packageCell);

    const statusCell = cell('whitespace-nowrap');
    statusCell.appendChild(statusPill(request.status));
    row.appendChild(statusCell);

    const created = new Date(request.createdAt);
    row.appendChild(cell('whitespace-nowrap text-ink-muted',
      `${created.toLocaleDateString()} ${created.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`));

    const actionsCell = cell('w-12 text-right');
    const actions = rowActions(request, permissions);

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
      for (const [label, icon, title, url] of actions) {
        menu.appendChild(menuItem(label, icon, () => openDrawer(title, url)));
      }
      openRowMenu(trigger, menu);
    });
    trigger.addEventListener('keydown', (event) => event.stopPropagation());
    actionsCell.appendChild(trigger);

    row.appendChild(actionsCell);
    body.appendChild(row);
  }
}

async function search() {
  const searchError = el('#activation-search-error');
  showError(searchError, '');

  try {
    const params = queryParams();
    params.set('skip', String(currentSkip));
    params.set('take', String(currentPageSize));

    const result = await api.get(`/ActivationRequests/Search?${params}`);
    currentTotalCount = result.totalCount;

    renderResults(result.items);
    showEmptyState(result.items.length === 0);

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
const FILTER_LABELS = {
  status: 'Status',
  ispId: 'ISP',
  packageCode: 'Package',
  contractDurationMonths: 'Contract',
  submittedFrom: 'Submitted from',
  submittedTo: 'Submitted to'
};

/** Local midnight of a yyyy-mm-dd date, plus `addDays`, as an ISO instant. */
function localMidnight(date, addDays = 0) {
  const [year, month, day] = date.split('-').map(Number);
  return new Date(year, month - 1, day + addDays).toISOString();
}

/**
 * The search box and the applied filters as query parameters — the same for the grid and the
 * Excel export. Dates are the user's own local days: "to" includes the whole day.
 */
function queryParams() {
  const params = new URLSearchParams();
  if (currentSearch) params.set('search', currentSearch);

  for (const [key, value] of Object.entries(currentFilters)) {
    if (key === 'submittedFrom') {
      params.set('submittedFrom', localMidnight(value));
    } else if (key === 'submittedTo') {
      params.set('submittedBefore', localMidnight(value, 1));
    } else {
      params.set(key, value);
    }
  }

  return params;
}

function filterLabel(key, value) {
  const field = el(`#activation-filters-form [name="${key}"]`);
  if (field instanceof HTMLSelectElement) {
    const option = [...field.options].find((candidate) => candidate.value === value);
    return option ? option.textContent.trim() : value;
  }
  if (key === 'submittedFrom' || key === 'submittedTo') {
    const [year, month, day] = value.split('-').map(Number);
    return new Date(year, month - 1, day).toLocaleDateString();
  }
  return value;
}

function applyFilters(filters) {
  currentFilters = filters;
  for (const field of el('#activation-filters-form').elements) {
    if (field.name) {
      field.value = currentFilters[field.name] ?? '';
    }
  }
  currentSkip = 0;
  renderFilterChips();
  search();
}

function renderFilterChips() {
  const container = el('#activation-filter-chips');
  container.replaceChildren();

  const active = Object.entries(currentFilters);

  const countBadge = el('#activation-filters-count');
  countBadge.hidden = active.length === 0;
  countBadge.textContent = String(active.length);

  for (const [key, value] of active) {
    const label = `${FILTER_LABELS[key] ?? key}: ${filterLabel(key, value)}`;
    const chip = document.createElement('span');
    chip.className = 'filter-chip';
    const text = document.createElement('span');
    text.textContent = label;
    chip.appendChild(text);
    const clear = document.createElement('button');
    clear.type = 'button';
    clear.setAttribute('aria-label', `Clear ${label}`);
    clear.textContent = '×';
    clear.addEventListener('click', () => {
      const remaining = { ...currentFilters };
      delete remaining[key];
      applyFilters(remaining);
    });
    chip.appendChild(clear);
    container.appendChild(chip);
  }

  if (active.length > 0) {
    const clearAll = document.createElement('button');
    clearAll.type = 'button';
    clearAll.className = 'text-sm text-brand-600 underline';
    clearAll.textContent = 'Clear';
    clearAll.addEventListener('click', () => applyFilters({}));
    container.appendChild(clearAll);
  }
}

function initFilters() {
  const button = el('#activation-filters-button');
  const panel = el('#activation-filters-panel');
  const form = el('#activation-filters-form');

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

  form.addEventListener('submit', (event) => {
    event.preventDefault();
    const filters = {};
    for (const [key, value] of new FormData(form)) {
      if (String(value).trim()) {
        filters[key] = String(value).trim();
      }
    }
    panel.hidden = true;
    button.setAttribute('aria-expanded', 'false');
    applyFilters(filters);
  });

  el('#activation-filters-reset').addEventListener('click', () => form.reset());

  // All pages of the current search + filters, built server-side as an .xlsx download.
  el('#activation-export-button')?.addEventListener('click', () => {
    window.location.href = `/ActivationRequests/Export?${queryParams()}`;
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

// --- Empty grid placeholders (Views/Shared/_EmptyState.cshtml) ------------------------
// "Nothing yet" when no search or filter is active, "nothing matches" otherwise; the table
// itself is hidden while there are no rows.
function showEmptyState(isEmpty) {
  const filtered = Boolean(currentSearch || Object.keys(currentFilters).length > 0);
  el('#activation-search-results').closest('table').hidden = isEmpty;
  el('#activation-empty-wrapper').hidden = !isEmpty;
  el('#activation-empty-none').hidden = !(isEmpty && !filtered);
  el('#activation-empty-filtered').hidden = !(isEmpty && filtered);
}

el('#activation-empty-filtered [data-empty-clear]')?.addEventListener('click', () => {
  currentSearch = '';
  el('#activation-search-query').value = '';
  applyFilters({});
});

el('#activation-empty-none [data-empty-add]')?.addEventListener('click', () => el('#activation-add-button')?.click());

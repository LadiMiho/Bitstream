/**
 * Audit Logs (Controllers/AccessManagementController.cs): search, Filters popup with pills,
 * paging, the entry details drawer and Excel export — the same structure as the Activation and
 * Post-Activation grids. Read-only: nothing here changes an audit entry.
 */
import { api, ApiError, reportClientError } from '../api-client.js';
import { openDrawer } from '../drawer.js';
import { createGridRefresher } from '../grid-auto-refresh.js';

const BASE = '/AccessManagement/AuditLog';

function showError(target, message) {
  if (!target) {
    return;
  }
  target.textContent = message;
  target.hidden = !message;
}

// --- Grid: search, Filters popup + pills, paging, export (same structure as activation-admin.js)
let currentSkip = 0;
let currentSearch = '';
// The Filters popup's applied values, keyed by the form field / query parameter name.
let currentFilters = {};
let currentPageSize = 20;
let currentTotalCount = 0;

const el = (selector) => document.querySelector(selector);

const ICONS = {
  view: '<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.6"><path d="M1.5 10S4.5 4 10 4s8.5 6 8.5 6-3 6-8.5 6-8.5-6-8.5-6Z" stroke-linejoin="round"/><circle cx="10" cy="10" r="2.5"/></svg>',
  kebab: '<svg viewBox="0 0 20 20" fill="currentColor" class="h-4 w-4"><circle cx="10" cy="4" r="1.5"/><circle cx="10" cy="10" r="1.5"/><circle cx="10" cy="16" r="1.5"/></svg>'
};

const FILTER_LABELS = {
  actor: 'User',
  entityType: 'Record type',
  actionCode: 'Action',
  from: 'From',
  to: 'To'
};

function describeError(error) {
  if (!(error instanceof ApiError)) {
    reportClientError('audit-log', error);
    return 'Something went wrong. Please try again.';
  }
  const fieldMessages = Object.values(error.errors).flat();
  return fieldMessages.length > 0 ? fieldMessages.join(' ') : error.message;
}

let openMenuTrigger = null;

function closeAllMenus() {
  document.querySelectorAll('.menu-panel').forEach((panel) => panel.remove());
  openMenuTrigger = null;
}

/** Row menus are appended to <body> with fixed positioning, so they never resize the scrolling grid. */
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

function menuItem(label, icon, handler) {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'menu-item';
  button.innerHTML = `${icon}<span>${label}</span>`;
  button.addEventListener('click', (event) => {
    event.stopPropagation();
    closeAllMenus();
    handler();
  });
  return button;
}

function openView(auditId) {
  openDrawer('Audit entry', `${BASE}/${encodeURIComponent(auditId)}/ViewDrawer`);
}

function cell(className, text) {
  const td = document.createElement('td');
  td.className = `table-cell ${className}`.trim();
  if (text !== undefined) {
    td.textContent = text;
  }
  return td;
}

function renderResults(items) {
  const body = el('#audit-search-results');
  body.replaceChildren();

  for (const entry of items) {
    const row = document.createElement('tr');
    row.className = 'cursor-pointer border-t border-line hover:bg-surface-muted';
    row.tabIndex = 0;
    row.setAttribute('aria-label', `View ${entry.actionCode}`);
    row.addEventListener('click', () => openView(entry.auditId));
    row.addEventListener('keydown', (event) => {
      if (event.key === 'Enter' && event.target === row) {
        openView(entry.auditId);
      }
    });

    const at = new Date(entry.timestamp);
    row.appendChild(cell('whitespace-nowrap text-ink-muted',
      `${at.toLocaleDateString()} ${at.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })}`));

    const actor = cell('');
    const name = document.createElement('div');
    name.className = entry.isSystem ? 'text-ink-muted italic' : 'text-ink';
    name.textContent = entry.actorName;
    actor.appendChild(name);
    if (entry.actorIp) {
      const ip = document.createElement('div');
      ip.className = 'font-mono text-xs text-ink-muted';
      ip.textContent = entry.actorIp;
      actor.appendChild(ip);
    }
    row.appendChild(actor);

    row.appendChild(cell('font-mono text-xs text-ink', entry.actionCode));

    const record = cell('');
    const type = document.createElement('div');
    type.className = 'text-ink';
    type.textContent = entry.entityType;
    record.appendChild(type);
    if (entry.entityId) {
      const id = document.createElement('div');
      id.className = 'font-mono text-xs text-ink-muted';
      id.textContent = entry.entityId;
      record.appendChild(id);
    }
    row.appendChild(record);

    const actionsCell = cell('w-12 text-right');
    const trigger = document.createElement('button');
    trigger.type = 'button';
    trigger.className = 'icon-button';
    trigger.setAttribute('aria-label', `Actions for entry ${entry.auditId}`);
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
      menu.appendChild(menuItem('View', ICONS.view, () => openView(entry.auditId)));
      openRowMenu(trigger, menu);
    });
    trigger.addEventListener('keydown', (event) => event.stopPropagation());
    actionsCell.appendChild(trigger);
    row.appendChild(actionsCell);

    body.appendChild(row);
  }
}

/** Local midnight of a yyyy-mm-dd date, plus `addDays`, as an ISO instant. */
function localMidnight(date, addDays = 0) {
  const [year, month, day] = date.split('-').map(Number);
  return new Date(year, month - 1, day + addDays).toISOString();
}

/** The search box and the applied filters as query parameters — the same for the grid and the export. */
function queryParams() {
  const params = new URLSearchParams();
  if (currentSearch) params.set('search', currentSearch);

  for (const [key, value] of Object.entries(currentFilters)) {
    if (key === 'from') {
      params.set('from', localMidnight(value));
    } else if (key === 'to') {
      params.set('before', localMidnight(value, 1));
    } else {
      params.set(key, value);
    }
  }

  return params;
}

let refresher = null;

async function fetchPage() {
  const params = queryParams();
  params.set('skip', String(currentSkip));
  params.set('take', String(currentPageSize));
  return api.get(`${BASE}/Search?${params}`);
}

function renderPage(result) {
  currentTotalCount = result.totalCount;
  renderResults(result.items);
  showEmptyState(result.items.length === 0);

  const shown = result.items.length === 0 ? 0 : currentSkip + 1;
  const shownTo = currentSkip + result.items.length;
  el('#audit-search-summary').textContent =
    result.totalCount === 0 ? 'No results' : `Showing ${shown}–${shownTo} of ${result.totalCount}`;
  el('#audit-search-prev').disabled = currentSkip === 0;
  el('#audit-search-next').disabled = currentSkip + currentPageSize >= result.totalCount;
  el('#audit-page-number').textContent = String(Math.floor(currentSkip / currentPageSize) + 1);
}

/** Loads the grid; it also reloads itself every minute (grid-auto-refresh.js). */
function search() {
  return refresher.load();
}

function showEmptyState(isEmpty) {
  const filtered = Boolean(currentSearch || Object.keys(currentFilters).length > 0);
  el('#audit-search-results').closest('table').hidden = isEmpty;
  el('#audit-empty-wrapper').hidden = !isEmpty;
  el('#audit-empty-none').hidden = !(isEmpty && !filtered);
  el('#audit-empty-filtered').hidden = !(isEmpty && filtered);
}

// --- Filters popover ---------------------------------------------------------------------
function filterLabel(key, value) {
  const field = el(`#audit-filters-form [name="${key}"]`);
  if (field instanceof HTMLSelectElement) {
    const option = [...field.options].find((candidate) => candidate.value === value);
    return option ? option.textContent.trim() : value;
  }
  if (key === 'from' || key === 'to') {
    const [year, month, day] = value.split('-').map(Number);
    return new Date(year, month - 1, day).toLocaleDateString();
  }
  return value;
}

function applyFilters(filters) {
  currentFilters = filters;
  for (const field of el('#audit-filters-form').elements) {
    if (field.name) {
      field.value = currentFilters[field.name] ?? '';
    }
  }
  currentSkip = 0;
  renderFilterChips();
  search();
}

function renderFilterChips() {
  const container = el('#audit-filter-chips');
  container.replaceChildren();

  const active = Object.entries(currentFilters);
  const countBadge = el('#audit-filters-count');
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
  const button = el('#audit-filters-button');
  const panel = el('#audit-filters-panel');
  const form = el('#audit-filters-form');

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

  el('#audit-filters-reset').addEventListener('click', () => form.reset());

  // All pages of the current search + filters, built server-side as an .xlsx download.
  el('#audit-export-button')?.addEventListener('click', () => {
    window.location.href = `${BASE}/Export?${queryParams()}`;
  });
}

function init() {
  const root = el('[data-role="audit-log-page"]');
  if (!root) {
    return;
  }

  el('#audit-search-form').addEventListener('submit', (event) => {
    event.preventDefault();
    currentSearch = el('#audit-search-query').value.trim();
    currentSkip = 0;
    search();
  });

  el('#audit-page-size').addEventListener('change', (event) => {
    currentPageSize = Number(event.target.value);
    currentSkip = 0;
    search();
  });

  el('#audit-search-prev').addEventListener('click', () => {
    currentSkip = Math.max(0, currentSkip - currentPageSize);
    search();
  });
  el('#audit-search-next').addEventListener('click', () => {
    if (currentSkip + currentPageSize < currentTotalCount) {
      currentSkip += currentPageSize;
      search();
    }
  });

  el('#audit-empty-filtered [data-empty-clear]')?.addEventListener('click', () => {
    currentSearch = '';
    el('#audit-search-query').value = '';
    applyFilters({});
  });

  refresher = createGridRefresher({
    statusElement: el('#audit-refresh-status'),
    button: el('#audit-refresh-button'),
    errorElement: el('#audit-search-error'),
    fetchPage,
    render: renderPage,
    describeError,
    isMenuOpen: () => openMenuTrigger !== null
  });

  initFilters();
  search();
}

init();

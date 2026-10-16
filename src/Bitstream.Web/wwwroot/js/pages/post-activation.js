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
    fillSelect(level3, selected ? selected.children : [], selected ? 'Select the detail' : 'Select a category first', (item) => item.name);
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
    currentSkip = 0;
    search();
  } catch (error) {
    showFieldErrors(form, error);
  } finally {
    if (submit) {
      submit.disabled = false;
    }
  }
});

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
  status: 'Status',
  ispId: 'ISP',
  classification1Code: 'Request type',
  classification2Code: 'Category',
  submittedFrom: 'Submitted from',
  submittedTo: 'Submitted to'
};

function describeError(error) {
  if (!(error instanceof ApiError)) {
    reportClientError('post-activation', error);
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

function openView(publicId) {
  openDrawer(`Request ${publicId}`, `${BASE}/Requests/${encodeURIComponent(publicId)}/ViewDrawer`);
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
  const showIsp = el('[data-role="post-activation-page"]').dataset.showIsp === 'true';
  const body = el('#pa-search-results');
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
    row.appendChild(cell('font-mono text-xs', request.phonenumber));

    const classification = cell('');
    const detail = document.createElement('div');
    detail.className = 'text-ink';
    detail.textContent = request.classification3Name;
    const path = document.createElement('div');
    path.className = 'text-xs text-ink-muted';
    path.textContent = `${request.classification1Name} › ${request.classification2Name}`;
    classification.append(detail, path);
    row.appendChild(classification);

    const statusCell = cell('whitespace-nowrap');
    const pill = document.createElement('span');
    pill.className = request.statusClass;
    pill.textContent = request.statusLabel;
    statusCell.appendChild(pill);
    row.appendChild(statusCell);

    row.appendChild(cell('font-mono text-xs', request.crmTicketId ?? '—'));

    const created = new Date(request.createdAt);
    row.appendChild(cell('whitespace-nowrap text-ink-muted',
      `${created.toLocaleDateString()} ${created.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`));

    const actionsCell = cell('w-12 text-right');
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
      menu.appendChild(menuItem('View', ICONS.view, () => openView(request.publicId)));
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

async function search() {
  const searchError = el('#pa-search-error');
  showError(searchError, '');

  try {
    const params = queryParams();
    params.set('skip', String(currentSkip));
    params.set('take', String(currentPageSize));

    const result = await api.get(`${BASE}/Search?${params}`);
    currentTotalCount = result.totalCount;

    renderResults(result.items);
    showEmptyState(result.items.length === 0);

    const shown = result.items.length === 0 ? 0 : currentSkip + 1;
    const shownTo = currentSkip + result.items.length;
    el('#pa-search-summary').textContent =
      result.totalCount === 0 ? 'No results' : `Showing ${shown}–${shownTo} of ${result.totalCount}`;
    el('#pa-search-prev').disabled = currentSkip === 0;
    el('#pa-search-next').disabled = currentSkip + currentPageSize >= result.totalCount;
    el('#pa-page-number').textContent = String(Math.floor(currentSkip / currentPageSize) + 1);
  } catch (error) {
    showError(searchError, describeError(error));
  }
}

function showEmptyState(isEmpty) {
  const filtered = Boolean(currentSearch || Object.keys(currentFilters).length > 0);
  el('#pa-search-results').closest('table').hidden = isEmpty;
  el('#pa-empty-wrapper').hidden = !isEmpty;
  el('#pa-empty-none').hidden = !(isEmpty && !filtered);
  el('#pa-empty-filtered').hidden = !(isEmpty && filtered);
}

// --- Filters popover ---------------------------------------------------------------------
function filterLabel(key, value) {
  const field = el(`#pa-filters-form [name="${key}"]`);
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
  for (const field of el('#pa-filters-form').elements) {
    if (field.name) {
      field.value = currentFilters[field.name] ?? '';
    }
  }
  currentSkip = 0;
  renderFilterChips();
  search();
}

function renderFilterChips() {
  const container = el('#pa-filter-chips');
  container.replaceChildren();

  const active = Object.entries(currentFilters);
  const countBadge = el('#pa-filters-count');
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
  const button = el('#pa-filters-button');
  const panel = el('#pa-filters-panel');
  const form = el('#pa-filters-form');

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

  el('#pa-filters-reset').addEventListener('click', () => form.reset());

  // All pages of the current search + filters, built server-side as an .xlsx download.
  el('#pa-export-button')?.addEventListener('click', () => {
    window.location.href = `${BASE}/Export?${queryParams()}`;
  });
}

function init() {
  const root = el('[data-role="post-activation-page"]');
  if (!root) {
    return;
  }

  root.addEventListener('click', (event) => {
    if (event.target.closest('[data-post-activation-action="new"]')) {
      openDrawer('New post-activation request', `${BASE}/NewDrawer`);
    }
  });

  el('#pa-search-form').addEventListener('submit', (event) => {
    event.preventDefault();
    currentSearch = el('#pa-search-query').value.trim();
    currentSkip = 0;
    search();
  });

  el('#pa-page-size').addEventListener('change', (event) => {
    currentPageSize = Number(event.target.value);
    currentSkip = 0;
    search();
  });

  el('#pa-search-prev').addEventListener('click', () => {
    currentSkip = Math.max(0, currentSkip - currentPageSize);
    search();
  });
  el('#pa-search-next').addEventListener('click', () => {
    if (currentSkip + currentPageSize < currentTotalCount) {
      currentSkip += currentPageSize;
      search();
    }
  });

  el('#pa-empty-filtered [data-empty-clear]')?.addEventListener('click', () => {
    currentSearch = '';
    el('#pa-search-query').value = '';
    applyFilters({});
  });

  initFilters();
  search();
}

init();

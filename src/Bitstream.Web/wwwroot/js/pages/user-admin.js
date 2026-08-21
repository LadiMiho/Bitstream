/**
 * User administration behaviour: create, search/browse, lock/unlock — each a direct call to
 * the existing /api/v1/users endpoints (Bitstream.Web/Endpoints/AdministrationEndpoints.cs).
 * No backend logic is duplicated here; every validation message shown comes from the API's own
 * response, and the search results are exactly what GET /api/v1/users returns — this script
 * only renders them and tracks paging offsets.
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

function renderUser(user) {
  el('#user-detail-id').textContent = user.userId;
  el('#user-detail-status').textContent = user.status;
  el('#user-detail-full-name').textContent = user.fullName;
  el('#user-detail-email').textContent = user.email;
  el('#user-detail-mobile').textContent = user.mobile;
  el('#user-detail-role').textContent = user.role;
  el('#user-detail-isp-id').textContent = user.ispId ?? '(internal user)';
  el('#user-detail-last-login').textContent = user.lastLoginAt
    ? new Date(user.lastLoginAt).toLocaleString()
    : 'Never';
  el('#user-detail').hidden = false;
  el('#user-detail').dataset.userId = user.userId;
}

async function setStatus(userId, status) {
  const statusError = el('#user-status-error');
  showError(statusError, '');

  try {
    await api.patch(`/api/v1/users/${userId}/status`, { status });
    const user = await api.get(`/api/v1/users/${userId}`);
    renderUser(user);
    await search(currentSkip, currentSearch);
  } catch (error) {
    showError(statusError, describeError(error));
  }
}

let currentSkip = 0;
let currentSearch = '';
let currentTotalCount = 0;

function renderResults(items) {
  const body = el('#user-search-results');
  body.replaceChildren();

  for (const user of items) {
    const row = document.createElement('tr');
    row.className = 'cursor-pointer hover:bg-surface-muted';
    row.dataset.userId = user.userId;

    for (const value of [user.fullName, user.email, user.role, user.status]) {
      const cell = document.createElement('td');
      cell.className = 'table-cell';
      cell.textContent = value;
      row.appendChild(cell);
    }

    row.addEventListener('click', () => renderUser(user));
    body.appendChild(row);
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

function init() {
  const root = el('[data-role="user-admin-page"]');
  if (!root) {
    return;
  }

  const createForm = el('#user-create-form');
  createForm?.addEventListener('submit', async (event) => {
    event.preventDefault();
    const createError = el('#user-create-error');
    showError(createError, '');

    const ispIdRaw = el('#user-create-isp-id').value.trim();

    const body = {
      ispId: ispIdRaw === '' ? null : Number(ispIdRaw),
      fullName: el('#user-create-full-name').value.trim(),
      email: el('#user-create-email').value.trim(),
      mobile: el('#user-create-mobile').value.trim(),
      roleName: el('#user-create-role').value,
      initialPassword: el('#user-create-password').value
    };

    try {
      const user = await api.post('/api/v1/users', body);
      createForm.reset();
      renderUser(user);
      await search(0, currentSearch);
    } catch (error) {
      showError(createError, describeError(error));
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

  el('#user-lock-button')?.addEventListener('click', () => {
    const userId = el('#user-detail').dataset.userId;
    setStatus(userId, 'Locked');
  });

  el('#user-unlock-button')?.addEventListener('click', () => {
    const userId = el('#user-detail').dataset.userId;
    setStatus(userId, 'Active');
  });

  search(0, '');
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', init, { once: true });
} else {
  init();
}

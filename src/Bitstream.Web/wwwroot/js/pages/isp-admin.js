/**
 * ISP administration behaviour: create, search/browse, lock/unlock — each a direct call to the
 * existing /api/v1/isps endpoints (Bitstream.Web/Endpoints/AdministrationEndpoints.cs). No
 * backend logic is duplicated here; every validation message shown comes from the API's own
 * response, and the search results are exactly what GET /api/v1/isps returns — this script only
 * renders them and tracks paging offsets.
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

function renderIsp(isp) {
  el('#isp-detail-id').textContent = isp.ispId;
  el('#isp-detail-status').textContent = isp.status;
  el('#isp-detail-name').textContent = isp.name;
  el('#isp-detail-nipt').textContent = isp.nipt;
  el('#isp-detail-contact-person').textContent = isp.contactPerson;
  el('#isp-detail-contact-email').textContent = isp.contactEmail;
  el('#isp-detail-contact-mobile').textContent = isp.contactMobile;
  el('#isp-detail-crm-bp').textContent = isp.crmBpReference;
  el('#isp-detail').hidden = false;
  el('#isp-detail').dataset.ispId = isp.ispId;
}

async function setStatus(ispId, status) {
  const statusError = el('#isp-status-error');
  showError(statusError, '');

  try {
    await api.patch(`/api/v1/isps/${ispId}/status`, { status });
    const isp = await api.get(`/api/v1/isps/${ispId}`);
    renderIsp(isp);
    await search(currentSkip, currentSearch);
  } catch (error) {
    showError(statusError, describeError(error));
  }
}

let currentSkip = 0;
let currentSearch = '';
let currentTotalCount = 0;

function renderResults(items) {
  const body = el('#isp-search-results');
  body.replaceChildren();

  for (const isp of items) {
    const row = document.createElement('tr');
    row.className = 'cursor-pointer hover:bg-surface-muted';
    row.dataset.ispId = isp.ispId;

    for (const value of [isp.name, isp.nipt, isp.status]) {
      const cell = document.createElement('td');
      cell.className = 'table-cell';
      cell.textContent = value;
      row.appendChild(cell);
    }

    row.addEventListener('click', () => renderIsp(isp));
    body.appendChild(row);
  }
}

async function search(skip, searchTerm) {
  const searchError = el('#isp-search-error');
  showError(searchError, '');

  try {
    const params = new URLSearchParams({ skip: String(skip), take: String(PAGE_SIZE) });
    if (searchTerm) {
      params.set('search', searchTerm);
    }

    const result = await api.get(`/api/v1/isps?${params}`);
    currentSkip = skip;
    currentSearch = searchTerm;
    currentTotalCount = result.totalCount;

    renderResults(result.items);
    el('#isp-search-empty').hidden = result.items.length > 0;

    const shown = result.items.length === 0 ? 0 : skip + 1;
    const shownTo = skip + result.items.length;
    el('#isp-search-summary').textContent =
      result.totalCount === 0 ? 'No results' : `${shown}–${shownTo} of ${result.totalCount}`;
    el('#isp-search-prev').disabled = skip === 0;
    el('#isp-search-next').disabled = skip + PAGE_SIZE >= result.totalCount;
  } catch (error) {
    showError(searchError, describeError(error));
  }
}

function init() {
  const root = el('[data-role="isp-admin-page"]');
  if (!root) {
    return;
  }

  const createForm = el('#isp-create-form');
  createForm?.addEventListener('submit', async (event) => {
    event.preventDefault();
    const createError = el('#isp-create-error');
    showError(createError, '');

    const body = {
      name: el('#isp-create-name').value.trim(),
      nipt: el('#isp-create-nipt').value.trim(),
      contactPerson: el('#isp-create-contact-person').value.trim(),
      contactEmail: el('#isp-create-contact-email').value.trim(),
      contactMobile: el('#isp-create-contact-mobile').value.trim(),
      crmBpReference: el('#isp-create-crm-bp').value.trim()
    };

    try {
      const isp = await api.post('/api/v1/isps', body);
      createForm.reset();
      renderIsp(isp);
      await search(0, currentSearch);
    } catch (error) {
      showError(createError, describeError(error));
    }
  });

  el('#isp-search-form').addEventListener('submit', (event) => {
    event.preventDefault();
    search(0, el('#isp-search-query').value.trim());
  });

  el('#isp-search-prev').addEventListener('click', () => search(Math.max(0, currentSkip - PAGE_SIZE), currentSearch));
  el('#isp-search-next').addEventListener('click', () => {
    if (currentSkip + PAGE_SIZE < currentTotalCount) {
      search(currentSkip + PAGE_SIZE, currentSearch);
    }
  });

  el('#isp-lock-button')?.addEventListener('click', () => {
    const ispId = el('#isp-detail').dataset.ispId;
    setStatus(ispId, 'Locked');
  });

  el('#isp-unlock-button')?.addEventListener('click', () => {
    const ispId = el('#isp-detail').dataset.ispId;
    setStatus(ispId, 'Active');
  });

  search(0, '');
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', init, { once: true });
} else {
  init();
}

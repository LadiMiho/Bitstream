/**
 * The Notifications page (Controllers/NotificationsController.cs): every notification of the
 * signed-in user, newest first, with an All / Unread switch and paging. Clicking a row opens it
 * through /Notifications/{id}/Open (marked read, then the request is shown); the row's check
 * button marks it read without leaving the page; "Mark all as read" marks every one. The header
 * bell follows through the NOTIFICATIONS_CHANGED event.
 */
import { api } from '../api-client.js';
import { NOTIFICATIONS_CHANGED, relativeTime, fullTime } from '../notification-format.js';

const el = (selector) => document.querySelector(selector);

let filter = 'all';
let skip = 0;
let pageSize = 20;
let totalCount = 0;

function showError(message) {
  const target = el('#notifications-error');
  target.textContent = message;
  target.hidden = !message;
}

function cell(className) {
  const td = document.createElement('td');
  td.className = `table-cell ${className}`.trim();
  return td;
}

function renderRow(item) {
  const row = document.createElement('tr');
  row.className = `cursor-pointer border-t border-line transition-colors hover:bg-surface-muted ${item.read ? '' : 'bg-brand-50/60'}`;
  row.tabIndex = 0;
  row.setAttribute('aria-label', `${item.read ? '' : 'Unread: '}${item.title}. ${item.message}`);
  const open = () => { window.location.href = item.url; };
  row.addEventListener('click', open);
  row.addEventListener('keydown', (event) => {
    if (event.key === 'Enter' && event.target === row) {
      open();
    }
  });

  const dotCell = cell('w-8 pr-0');
  if (!item.read) {
    const dot = document.createElement('span');
    dot.className = 'notification-unread-dot inline-block';
    dot.title = 'Unread';
    dotCell.appendChild(dot);
  }
  row.appendChild(dotCell);

  const textCell = cell('min-w-[18rem]');
  const title = document.createElement('div');
  title.className = item.read ? 'font-medium text-ink' : 'font-semibold text-ink';
  title.textContent = item.title;
  const message = document.createElement('div');
  message.className = 'text-ink';
  message.textContent = item.message;
  textCell.append(title, message);
  if (item.detail) {
    const detail = document.createElement('div');
    detail.className = 'mt-0.5 line-clamp-2 text-xs text-ink-muted';
    detail.textContent = item.detail;
    textCell.appendChild(detail);
  }
  row.appendChild(textCell);

  const statusCell = cell('whitespace-nowrap');
  const pill = document.createElement('span');
  pill.className = item.statusClass;
  pill.textContent = item.statusLabel;
  statusCell.appendChild(pill);
  row.appendChild(statusCell);

  const timeCell = cell('whitespace-nowrap text-ink-muted');
  const time = document.createElement('time');
  time.dateTime = item.createdAt;
  time.title = fullTime(item.createdAt);
  time.textContent = relativeTime(item.createdAt);
  timeCell.appendChild(time);
  row.appendChild(timeCell);

  const actionCell = cell('w-12 text-right');
  if (!item.read) {
    const markRead = document.createElement('button');
    markRead.type = 'button';
    markRead.className = 'icon-button';
    markRead.title = 'Mark as read';
    markRead.setAttribute('aria-label', `Mark ${item.title} as read`);
    markRead.innerHTML = '<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.7" aria-hidden="true"><path d="m4.5 10.5 3.5 3.5 7.5-8" stroke-linecap="round" stroke-linejoin="round"/></svg>';
    markRead.addEventListener('click', async (event) => {
      event.stopPropagation();
      markRead.disabled = true;
      try {
        await api.post(`/Notifications/${item.id}/Read`);
        document.dispatchEvent(new CustomEvent(NOTIFICATIONS_CHANGED, { detail: { source: 'page' } }));
        await load();
      } catch (error) {
        showError(error.message || 'The notification could not be marked as read.');
        markRead.disabled = false;
      }
    });
    markRead.addEventListener('keydown', (event) => event.stopPropagation());
    actionCell.appendChild(markRead);
  }
  row.appendChild(actionCell);

  return row;
}

async function load() {
  showError('');

  try {
    const result = await api.get(`/Notifications/Search?filter=${filter}&skip=${skip}&take=${pageSize}`);

    // Paging past the end (the last unread one on the last page was just read): step back.
    if (result.items.length === 0 && skip > 0) {
      skip = Math.max(0, skip - pageSize);
      return load();
    }

    totalCount = result.totalCount;
    const body = el('#notifications-results');
    body.replaceChildren(...result.items.map(renderRow));

    const empty = result.items.length === 0;
    body.closest('table').hidden = empty;
    el('#notifications-empty-wrapper').hidden = !empty;
    el('#notifications-empty-none').hidden = !(empty && filter === 'all');
    el('#notifications-empty-unread').hidden = !(empty && filter === 'unread');

    const unread = el('#notifications-unread-count');
    unread.hidden = result.unreadCount === 0;
    unread.textContent = String(result.unreadCount);
    el('#notifications-read-all').disabled = result.unreadCount === 0;

    const from = empty ? 0 : skip + 1;
    el('#notifications-summary').textContent = totalCount === 0 ? 'No notifications' : `Showing ${from}–${skip + result.items.length} of ${totalCount}`;
    el('#notifications-prev').disabled = skip === 0;
    el('#notifications-next').disabled = skip + pageSize >= totalCount;
    el('#notifications-page-number').textContent = String(Math.floor(skip / pageSize) + 1);
  } catch (error) {
    showError(error.message || 'The notifications could not be loaded.');
  }

  return undefined;
}

function setFilter(value) {
  filter = value;
  skip = 0;
  for (const button of document.querySelectorAll('[data-filter]')) {
    button.setAttribute('aria-pressed', String(button.dataset.filter === value));
  }
  load();
}

function init() {
  if (!el('[data-role="notifications-page"]')) {
    return;
  }

  for (const button of document.querySelectorAll('[data-filter]')) {
    button.addEventListener('click', () => setFilter(button.dataset.filter));
  }

  el('[data-empty-show-all]')?.addEventListener('click', () => setFilter('all'));

  el('#notifications-page-size').addEventListener('change', (event) => {
    pageSize = Number(event.target.value);
    skip = 0;
    load();
  });
  el('#notifications-prev').addEventListener('click', () => {
    skip = Math.max(0, skip - pageSize);
    load();
  });
  el('#notifications-next').addEventListener('click', () => {
    if (skip + pageSize < totalCount) {
      skip += pageSize;
      load();
    }
  });

  el('#notifications-read-all').addEventListener('click', async (event) => {
    const button = event.currentTarget;
    button.disabled = true;
    try {
      await api.post('/Notifications/ReadAll');
      document.dispatchEvent(new CustomEvent(NOTIFICATIONS_CHANGED, { detail: { source: 'page' } }));
    } catch (error) {
      showError(error.message || 'The notifications could not be marked as read.');
    }
    await load();
  });

  // The bell's "Mark all as read" changes this list too.
  document.addEventListener(NOTIFICATIONS_CHANGED, (event) => {
    if (event.detail?.source === 'bell') {
      load();
    }
  });

  load();
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', init, { once: true });
} else {
  init();
}

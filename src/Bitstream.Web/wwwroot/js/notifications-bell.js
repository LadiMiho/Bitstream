/**
 * The header notification bell (Views/Shared/_Layout.cshtml): the unread count in a red badge
 * and, in its dropdown, the newest unread notifications. Opening and closing the dropdown is
 * nav.js's ([data-nav-menu]); this script fills it from /Notifications/Summary.
 * <p>
 * Each notification is a link to /Notifications/{id}/Open, which marks it read on the server and
 * then shows the request it is about. The count refreshes when the dropdown opens, every minute
 * while the tab is visible and the user is active, and after notifications are read on the
 * Notifications page. It stops refreshing after 15 minutes without activity so it does not keep
 * an abandoned session alive (TR-SEC-07), and for good once the session has ended.
 */
import { api, ApiError } from './api-client.js';
import { NOTIFICATIONS_CHANGED, relativeTime, fullTime } from './notification-format.js';

const REFRESH_INTERVAL_MS = 60 * 1000;
const AWAY_AFTER_MS = 15 * 60 * 1000;

const root = document.querySelector('[data-role="notification-bell"]');

if (root) {
  const trigger = root.querySelector('[data-bell-trigger]');
  const badge = root.querySelector('[data-bell-badge]');
  const list = root.querySelector('[data-bell-list]');
  const readAll = root.querySelector('[data-bell-read-all]');

  let lastUserActivity = Date.now();
  let stopped = false;
  let loading = false;

  const render = (summary) => {
    const count = summary.unreadCount;
    badge.hidden = count === 0;
    badge.textContent = count > 99 ? '99+' : String(count);
    trigger.setAttribute('aria-label', count === 0 ? 'Notifications, none unread' : `Notifications, ${count} unread`);
    readAll.disabled = count === 0;

    list.replaceChildren();

    if (summary.items.length === 0) {
      const empty = document.createElement('div');
      empty.className = 'px-6 py-10 text-center';
      empty.innerHTML =
        '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" class="mx-auto h-8 w-8 text-ink-muted" aria-hidden="true">' +
        '<path d="M20 6 9 17l-5-5" stroke-linecap="round" stroke-linejoin="round"/></svg>';
      const title = document.createElement('p');
      title.className = 'mt-2 text-sm font-medium text-ink';
      title.textContent = "You're all caught up";
      const text = document.createElement('p');
      text.className = 'mt-1 text-xs text-ink-muted';
      text.textContent = 'New notifications will appear here.';
      empty.append(title, text);
      list.appendChild(empty);
      return;
    }

    for (const item of summary.items) {
      list.appendChild(renderItem(item));
    }

    if (count > summary.items.length) {
      const more = document.createElement('p');
      more.className = 'px-4 py-2 text-center text-xs text-ink-muted';
      more.textContent = `and ${count - summary.items.length} more unread`;
      list.appendChild(more);
    }
  };

  const load = async () => {
    if (stopped || loading) {
      return;
    }

    loading = true;
    try {
      render(await api.get('/Notifications/Summary'));
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) {
        stopped = true;
      }
      console.warn('Notifications could not be loaded', error);
    } finally {
      loading = false;
    }
  };

  trigger.addEventListener('click', () => {
    if (trigger.getAttribute('aria-expanded') === 'true') {
      load();
    }
  });

  readAll.addEventListener('click', async () => {
    readAll.disabled = true;
    try {
      await api.post('/Notifications/ReadAll');
      document.dispatchEvent(new CustomEvent(NOTIFICATIONS_CHANGED, { detail: { source: 'bell' } }));
    } catch (error) {
      console.warn('Could not mark the notifications read', error);
    }
    await load();
  });

  document.addEventListener(NOTIFICATIONS_CHANGED, (event) => {
    if (event.detail?.source !== 'bell') {
      load();
    }
  });

  for (const type of ['pointerdown', 'keydown', 'wheel', 'touchstart']) {
    document.addEventListener(type, () => { lastUserActivity = Date.now(); }, { capture: true, passive: true });
  }

  document.addEventListener('visibilitychange', () => {
    if (!document.hidden) {
      load();
    }
  });

  setInterval(() => {
    if (!document.hidden && Date.now() - lastUserActivity <= AWAY_AFTER_MS) {
      load();
    }
  }, REFRESH_INTERVAL_MS);

  load();
}

function renderItem(item) {
  const link = document.createElement('a');
  link.className = 'notification-item';
  link.href = item.url;
  link.setAttribute('role', 'listitem');

  const dot = document.createElement('span');
  dot.className = 'notification-unread-dot';
  dot.setAttribute('aria-hidden', 'true');

  const body = document.createElement('div');
  body.className = 'min-w-0 flex-1';

  const head = document.createElement('div');
  head.className = 'flex items-start justify-between gap-2';
  const title = document.createElement('p');
  title.className = 'truncate text-sm font-semibold text-ink';
  title.textContent = item.title;
  const time = document.createElement('time');
  time.className = 'shrink-0 text-xs text-ink-muted';
  time.dateTime = item.createdAt;
  time.title = fullTime(item.createdAt);
  time.textContent = relativeTime(item.createdAt);
  head.append(title, time);

  const message = document.createElement('p');
  message.className = 'mt-0.5 text-sm text-ink';
  message.textContent = item.message;

  body.append(head, message);

  if (item.detail) {
    const detail = document.createElement('p');
    detail.className = 'mt-0.5 line-clamp-2 text-xs text-ink-muted';
    detail.textContent = item.detail;
    body.appendChild(detail);
  }

  const status = document.createElement('span');
  status.className = `${item.statusClass} mt-1.5`;
  status.textContent = item.statusLabel;
  body.appendChild(status);

  link.append(dot, body);
  return link;
}

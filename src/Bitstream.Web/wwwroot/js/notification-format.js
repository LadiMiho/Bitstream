/**
 * Shared by the header bell (notifications-bell.js) and the Notifications page
 * (pages/notifications.js): how a notification's time reads, and the event both send when the
 * user has read something, so the other refreshes its count.
 */

/** Fired on document after notifications were marked read, so the bell's count follows. */
export const NOTIFICATIONS_CHANGED = 'bitstream:notifications-changed';

const MINUTE = 60 * 1000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/** "Just now", "5 min ago", "3 h ago", "Yesterday", then the date. */
export function relativeTime(isoDate, now = new Date()) {
  const date = new Date(isoDate);
  const elapsed = now.getTime() - date.getTime();

  if (elapsed < MINUTE) {
    return 'Just now';
  }
  if (elapsed < HOUR) {
    return `${Math.floor(elapsed / MINUTE)} min ago`;
  }
  if (elapsed < DAY && date.getDate() === now.getDate()) {
    return `${Math.floor(elapsed / HOUR)} h ago`;
  }

  const yesterday = new Date(now.getFullYear(), now.getMonth(), now.getDate() - 1);
  if (date >= yesterday && date < new Date(now.getFullYear(), now.getMonth(), now.getDate())) {
    return `Yesterday, ${date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`;
  }

  return date.toLocaleDateString([], { day: 'numeric', month: 'short', year: date.getFullYear() === now.getFullYear() ? undefined : 'numeric' });
}

/** The full date and time, for a tooltip. */
export function fullTime(isoDate) {
  const date = new Date(isoDate);
  return `${date.toLocaleDateString()} ${date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`;
}

/**
 * Notification settings (Access Management, notification.manage): saves every switch that is on
 * (each one an event code and an audience); the server turns every other one off.
 */
import { api } from '../api-client.js';

const el = (selector) => document.querySelector(selector);

function init() {
  if (!el('[data-role="notification-settings-page"]')) {
    return;
  }

  const save = el('#notification-settings-save');
  const saved = el('#notification-settings-saved');
  const error = el('#notification-settings-error');
  let savedTimer = null;

  document.addEventListener('change', (event) => {
    if (event.target.matches('[data-event-code]')) {
      saved.hidden = true;
    }
  });

  save.addEventListener('click', async () => {
    const enabled = [...document.querySelectorAll('[data-event-code]:checked')]
      .map((box) => ({ eventCode: box.dataset.eventCode, audience: box.dataset.audience }));

    save.disabled = true;
    error.hidden = true;
    try {
      await api.post('/AccessManagement/NotificationSettings', { enabled });
      saved.hidden = false;
      clearTimeout(savedTimer);
      savedTimer = setTimeout(() => { saved.hidden = true; }, 4000);
    } catch (failure) {
      error.textContent = failure.message || 'The settings could not be saved.';
      error.hidden = false;
    } finally {
      save.disabled = false;
    }
  });
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', init, { once: true });
} else {
  init();
}

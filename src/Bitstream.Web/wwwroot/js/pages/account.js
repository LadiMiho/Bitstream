/**
 * My account (Controllers/AccountController.cs): the change-password form. The server checks the
 * current password and the password policy; this only shows its answer next to each field.
 */
import { api, ApiError, reportClientError } from '../api-client.js';

const el = (selector) => document.querySelector(selector);

function showError(target, message) {
  if (!target) {
    return;
  }
  target.textContent = message;
  target.hidden = !message;
}

function init() {
  const form = el('#account-password-form');
  if (!form) {
    return;
  }

  const success = el('#account-password-success');

  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    form.querySelectorAll('[data-field-error]').forEach((target) => showError(target, ''));
    success.hidden = true;

    const value = (name) => form.querySelector(`[name=${name}]`).value;
    const body = { currentPassword: value('currentPassword'), newPassword: value('newPassword'), confirmPassword: value('confirmPassword') };

    const missing = [['currentPassword', 'Enter your current password.'], ['newPassword', 'Enter a new password.'], ['confirmPassword', 'Enter the new password again.']]
      .filter(([name]) => !body[name]);
    if (missing.length > 0) {
      missing.forEach(([name, message]) => showError(form.querySelector(`[data-field-error="${name}"]`), message));
      form.querySelector(`[name=${missing[0][0]}]`).focus();
      return;
    }

    if (body.newPassword !== body.confirmPassword) {
      showError(form.querySelector('[data-field-error="confirmPassword"]'), 'The two new passwords are not the same.');
      form.querySelector('[name=confirmPassword]').focus();
      return;
    }

    const button = form.querySelector('button[type=submit]');
    button.disabled = true;

    try {
      await api.post('/Account/Password', body);
      form.reset();
      success.hidden = false;
      el('#account-password-updated').textContent = 'Just now';
    } catch (error) {
      if (!(error instanceof ApiError)) {
        reportClientError('account', error);
        showError(form.querySelector('[data-field-error="request"]'), 'Something went wrong. Please try again.');
        return;
      }

      const unmatched = [];
      for (const [field, messages] of Object.entries(error.errors || {})) {
        const target = form.querySelector(`[data-field-error="${field}"]`);
        if (target) {
          showError(target, messages.join(' '));
        } else {
          unmatched.push(...messages);
        }
      }
      if (unmatched.length > 0 || Object.keys(error.errors || {}).length === 0) {
        showError(form.querySelector('[data-field-error="request"]'), unmatched.length > 0 ? unmatched.join(' ') : error.message);
      }
    } finally {
      button.disabled = false;
    }
  });
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', init, { once: true });
} else {
  init();
}

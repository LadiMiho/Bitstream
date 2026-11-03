/**
 * The user menu's My account and Change password items (Views/Shared/_Layout.cshtml), on every
 * page: each opens its own drawer (Controllers/AccountController.cs), and this submits the Change
 * password drawer's form. The server checks the current password and the password policy; this
 * only shows its answer next to each field.
 */
import { api, ApiError, reportClientError } from './api-client.js';
import { openDrawer, drawerBody, drawerFooter } from './drawer.js';

const DRAWERS = {
  account: ['My account', '/Account/ViewDrawer'],
  password: ['Change password', '/Account/PasswordDrawer']
};

function showError(target, message) {
  if (!target) {
    return;
  }
  target.textContent = message;
  target.hidden = !message;
}

/** Closes the header dropdown the item was in, as nav.js would on an outside click. */
function closeMenu(item) {
  const menu = item.closest('[data-nav-menu]');
  if (!menu) {
    return;
  }
  menu.querySelector('[data-nav-panel]').hidden = true;
  menu.querySelector('[data-nav-trigger]')?.setAttribute('aria-expanded', 'false');
}

// Delegated: the items live in the user menu, and "Change password" also in the My account
// drawer's footer, which is re-filled on every openDrawer(). Capture phase, because the header
// dropdown stops clicks inside it from bubbling (nav.js).
document.addEventListener('click', (event) => {
  const item = event.target.closest('[data-account-drawer]');
  if (!item) {
    return;
  }

  const [title, url] = DRAWERS[item.dataset.accountDrawer] || [];
  if (!url) {
    return;
  }

  closeMenu(item);
  openDrawer(title, url);
}, { capture: true });

document.addEventListener('submit', async (event) => {
  const form = event.target;
  if (!(form instanceof HTMLFormElement) || form.dataset.role !== 'account-password-form') {
    return;
  }

  event.preventDefault();
  form.querySelectorAll('[data-field-error]').forEach((target) => showError(target, ''));

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

  const submit = drawerFooter.querySelector('button[type=submit]');
  if (submit) {
    submit.disabled = true;
  }

  try {
    await api.post('/Account/Password', body);

    const done = document.createElement('div');
    done.className = 'rounded-md border border-state-done/30 bg-state-done/10 px-4 py-3 text-sm text-ink';
    done.setAttribute('role', 'status');
    done.innerHTML = '<p class="font-semibold text-state-done">Your password has been changed.</p>' +
      '<p class="mt-1">Use the new password the next time you sign in. Any other session of yours has been signed out.</p>';
    drawerBody.replaceChildren(done);

    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'btn-primary';
    close.dataset.drawerCancel = '';
    close.textContent = 'Close';
    drawerFooter.replaceChildren(close);
    close.focus();
  } catch (error) {
    if (!(error instanceof ApiError)) {
      reportClientError('account-password', error);
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
    if (submit) {
      submit.disabled = false;
    }
  }
});

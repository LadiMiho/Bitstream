/**
 * Login page behaviour: the two-factor sign-in flow against the existing
 * POST /api/v1/auth/login and POST /api/v1/auth/login/verify endpoints
 * (Bitstream.Web/Endpoints/AuthEndpoints.cs) — nothing here re-implements the credential
 * check, the lockout decision or the 2FA challenge; both live entirely server-side.
 *
 * The two steps live on one page and are shown/hidden by this script so a JS-issued
 * fetch can drive the two-request flow without a page reload in between — that is not this
 * script acting as a router, since there is no view to route to on this page. Once the
 * second factor succeeds, the browser is sent to the return URL via a real, ordinary
 * navigation (`window.location.href`), the same outcome a server-rendered redirect would
 * produce — this script does not intercept clicks or own routing beyond that one redirect.
 */
import { api, ApiError } from '../api-client.js';

function el(selector) {
  return document.querySelector(selector);
}

function showError(target, message) {
  target.textContent = message;
  target.hidden = !message;
}

function init() {
  const root = el('[data-role="login-page"]');
  if (!root) {
    return;
  }

  const returnUrl = root.dataset.returnUrl || '/';

  const credentialsForm = el('#login-credentials-form');
  const credentialsError = el('#login-credentials-error');

  const codeForm = el('#login-code-form');
  const codeError = el('#login-code-error');
  const codeChannel = el('#login-code-channel');

  /** Held only in memory for this page load — never written to storage or the DOM. */
  let challengeToken = null;

  credentialsForm.addEventListener('submit', async (event) => {
    event.preventDefault();
    showError(credentialsError, '');

    const email = el('#login-email').value.trim();
    const password = el('#login-password').value;
    const submitButton = credentialsForm.querySelector('button[type="submit"]');
    submitButton.disabled = true;

    try {
      const challenge = await api.post('/api/v1/auth/login', { email, password });
      challengeToken = challenge.challengeToken;
      codeChannel.textContent = describeChannel(challenge.channel);
      credentialsForm.hidden = true;
      codeForm.hidden = false;
      el('#login-code').focus();
    } catch (error) {
      showError(credentialsError, describeLoginError(error));
    } finally {
      submitButton.disabled = false;
    }
  });

  codeForm.addEventListener('submit', async (event) => {
    event.preventDefault();
    showError(codeError, '');

    const code = el('#login-code').value.trim();
    const submitButton = codeForm.querySelector('button[type="submit"]');
    submitButton.disabled = true;

    try {
      await api.post('/api/v1/auth/login/verify', { challengeToken, code });
      window.location.href = returnUrl;
    } catch (error) {
      showError(codeError, describeVerifyError(error));
      submitButton.disabled = false;
    }
  });
}

function describeChannel(channel) {
  switch (channel) {
    case 'Totp':
      return 'Enter the code from your authenticator app.';
    case 'EmailOtp':
      return 'A code was sent to your email address.';
    case 'SmsOtp':
      return 'A code was sent to your mobile number.';
    default:
      return 'Enter the verification code.';
  }
}

function describeLoginError(error) {
  if (!(error instanceof ApiError)) {
    return 'Something went wrong. Please try again.';
  }
  if (error.status === 423) {
    return error.message;
  }
  if (error.status === 503) {
    return error.message;
  }
  return 'The email or password is incorrect.';
}

function describeVerifyError(error) {
  if (!(error instanceof ApiError)) {
    return 'Something went wrong. Please try again.';
  }
  return error.message || 'The code is incorrect.';
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', init, { once: true });
} else {
  init();
}

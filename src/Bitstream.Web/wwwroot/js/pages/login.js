/**
 * Login page behaviour: the two-factor sign-in flow against
 * Controllers/AuthController.cs's Login and VerifyTwoFactor actions — nothing here re-implements the credential
 * check, the lockout decision or the 2FA verification; both live entirely server-side, via
 * ASP.NET Core Identity's own SignInManager. There is no challenge token to hold between the
 * two requests any more: Identity tracks which account is mid-two-factor with its own
 * short-lived cookie, sent and read automatically by the browser (same-origin fetch already
 * includes cookies) — this script only needs the verification code itself.
 *
 * The two steps live on one page and are shown/hidden by this script so a JS-issued
 * fetch can drive the two-request flow without a page reload in between — that is not this
 * script acting as a router, since there is no view to route to on this page. Once the
 * second factor succeeds, the browser is sent to the return URL via a real, ordinary
 * navigation (`window.location.href`), the same outcome a server-rendered redirect would
 * produce — this script does not intercept clicks or own routing beyond that one redirect.
 */
import { api, ApiError, reportClientError } from '../api-client.js';

function el(selector) {
  return document.querySelector(selector);
}

function showError(target, message) {
  target.textContent = message;
  target.hidden = !message;
}

// --- Remember me -----------------------------------------------------------------------------
// Only the email is kept, in this browser's localStorage. The password is handed to the
// browser's own password manager (Credential Management API, where the browser has it; other
// browsers offer to save it from the form's autocomplete attributes), never stored by the portal.
// The second factor is not remembered: the code is asked for at every sign-in.
const REMEMBERED_EMAIL_KEY = 'bitstream.login.rememberedEmail';

function readRememberedEmail() {
  try {
    return window.localStorage.getItem(REMEMBERED_EMAIL_KEY) || '';
  } catch {
    return '';
  }
}

function rememberLogin(remember, email, password) {
  try {
    if (remember) {
      window.localStorage.setItem(REMEMBERED_EMAIL_KEY, email);
    } else {
      window.localStorage.removeItem(REMEMBERED_EMAIL_KEY);
    }
  } catch {
    // Storage blocked (private window, site data cleared): nothing to remember with.
  }

  if (remember && 'PasswordCredential' in window && navigator.credentials?.store) {
    // Shows the browser's own "Save password?" prompt.
    navigator.credentials.store(new window.PasswordCredential({ id: email, password, name: email }))
      .catch(() => { /* the user declined, or the browser does not allow it here */ });
  }
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
  const totpEnrollment = el('#login-totp-enrollment');
  const totpQrImage = el('#login-totp-qr');
  const rememberBox = el('#login-remember');

  const rememberedEmail = readRememberedEmail();
  if (rememberedEmail) {
    el('#login-email').value = rememberedEmail;
    rememberBox.checked = true;
    el('#login-password').focus();
  }

  credentialsForm.addEventListener('submit', async (event) => {
    event.preventDefault();
    showError(credentialsError, '');

    const email = el('#login-email').value.trim();
    const password = el('#login-password').value;
    const submitButton = credentialsForm.querySelector('button[type="submit"]');
    submitButton.disabled = true;

    try {
      const challenge = await api.post('/Auth/Login', { email, password });
      // The email and password were accepted: remember them now (or forget them) whatever the
      // second factor turns out to be.
      rememberLogin(rememberBox.checked, email, password);

      if (!challenge.channel) {
        // Signed in without a second factor: the session cookie is already set.
        window.location.href = returnUrl;
        return;
      }

      codeChannel.textContent = describeChannel(challenge.channel, Boolean(challenge.qrCodeDataUri));

      if (challenge.qrCodeDataUri) {
        totpQrImage.src = challenge.qrCodeDataUri;
        totpEnrollment.hidden = false;
      } else {
        totpQrImage.removeAttribute('src');
        totpEnrollment.hidden = true;
      }

      credentialsForm.hidden = true;
      codeForm.hidden = false;
      el('#login-code').focus();
    } catch (error) {
      console.error('Login failed', error);
      if (!isExpectedLoginFailure(error)) {
        reportClientError('login', error);
      }
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
      await api.post('/Auth/Login/Verify', { code });

      // The sign-in cookie was just set; if the browser refused to keep it (a Secure-only cookie
      // on a plain http:// page), going on would only land back on this page. Check first.
      try {
        await api.get('/Auth/Me');
      } catch (sessionError) {
        if (sessionError instanceof ApiError && sessionError.status === 401) {
          reportClientError('login-session', new Error('Signed in, but the browser did not keep the session cookie (page opened over ' + window.location.protocol + ').'));
          showError(codeError, 'You were signed in, but your browser did not keep the session. The site is opened over '
            + window.location.protocol.replace(':', '') + ' and the session requires https. Open the site over https, '
            + 'or ask the administrator to allow http on this test server.');
          submitButton.disabled = false;
          return;
        }
        throw sessionError;
      }

      window.location.href = returnUrl;
    } catch (error) {
      console.error('Verification failed', error);
      if (!(error instanceof ApiError) || error.status === 0 || error.status >= 500) {
        reportClientError('login-verify', error);
      }
      showError(codeError, describeVerifyError(error));
      submitButton.disabled = false;
    }
  });
}

function describeChannel(channel, needsEnrollment) {
  switch (channel) {
    case 'Totp':
      return needsEnrollment
        ? 'Scan this QR code with your authenticator app, then enter the code it shows.'
        : 'Enter the code from your authenticator app.';
    case 'EmailOtp':
      return 'A code was sent to your email address.';
    case 'SmsOtp':
      return 'A code was sent to your mobile number.';
    default:
      return 'Enter the verification code.';
  }
}

/** Wrong password, unknown account or locked account: the normal "no", not a fault worth reporting. */
function isExpectedLoginFailure(error) {
  return error instanceof ApiError && [400, 401, 423].includes(error.status);
}

function describeLoginError(error) {
  if (!(error instanceof ApiError)) {
    // A script error after the server answered; the details are in the log file.
    return `Something went wrong (${error && error.message ? error.message : error}). Please try again.`;
  }
  if (error.status === 0 || error.status >= 500 || (error.status >= 200 && error.status < 300)) {
    // Not a credentials problem: say what actually happened rather than "wrong password".
    return error.message;
  }
  if (error.status === 423 || error.status === 503) {
    return error.message;
  }
  if (error.status === 429) {
    return 'Too many attempts. Please wait a moment and try again.';
  }
  return 'The email or password is incorrect.';
}

function describeVerifyError(error) {
  if (!(error instanceof ApiError)) {
    return `Something went wrong (${error && error.message ? error.message : error}). Please try again.`;
  }
  return error.message || 'The code is incorrect.';
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', init, { once: true });
} else {
  init();
}

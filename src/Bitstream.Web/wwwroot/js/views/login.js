/**
 * Placeholder login page. The auth-guard (js/auth-guard.js) sends every unauthenticated
 * visitor here; the real sign-in form — email/password, then the 2FA challenge, against
 * POST /api/v1/auth/login and /login/verify — is built in GUI-2.
 * @param {HTMLElement} container
 */
export default function renderLogin(container) {
  container.innerHTML = `
    <section class="card mx-auto max-w-md" aria-labelledby="login-heading">
      <h1 id="login-heading" class="text-xl font-semibold">Sign in</h1>
      <p class="mt-2 text-sm text-ink-muted">
        The sign-in screen has not been built yet. This placeholder exists so the auth-guard
        has somewhere to send a visitor without a session; it is replaced by the real screen,
        including the two-factor step, in GUI-2.
      </p>
    </section>
  `;
}

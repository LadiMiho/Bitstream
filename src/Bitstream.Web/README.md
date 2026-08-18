# Bitstream Portal — frontend

Vanilla JavaScript and Tailwind CSS. No framework, no bundler: the Tailwind CLI is the only
build step, and the browser loads the ES modules directly.

## Build

```bash
cd src/Bitstream.Web
npm ci
npm run build:css     # one-off, minified -> wwwroot/css/app.css
npm run watch:css     # rebuild on change while developing
```

`wwwroot/css/app.css` is generated and is not committed. Everything else under `wwwroot/` is
source.

## Layout

```
src/styles/app.css        Tailwind entry point: theme tokens and component classes
wwwroot/index.html        Application shell: header, top-level nav, #app-content
wwwroot/js/api.js         Fetch wrapper: correlation ID, ProblemDetails handling
wwwroot/js/router.js      Hash-based client-side router (no framework)
wwwroot/js/auth-guard.js  Session check + router guard, redirects an unauthenticated visitor to /login
wwwroot/js/app.js         Bootstrap: wires nav, routes, sign-out
wwwroot/js/views/         One module per page: access-management, activation-requests,
                          post-activation, reporting, login, not-found
wwwroot/css/app.css       Generated — do not edit
```

## Routing

Hash-based (`#/access-management`, not pushState) so a deep link works on a hard refresh
without a server-side catch-all route — the API host currently serves this folder as a plain
static file tree. `js/router.js` is the whole router: register a path and a view function with
`route(path, view)`, and it renders that view into `#app-content` on every `hashchange`.

`js/auth-guard.js` is wired in as the router's guard (`guardWith`) and runs before every
render. It calls `GET /api/v1/auth/me` on each navigation — not cached across them, since a
session can start or expire between route changes — and sends an unauthenticated visitor to
`/login`, or a signed-in visitor away from it. **This is presentation only** (TR-SEC-17): every
module's own API calls are authorised server-side regardless of what the guard let through.
`/login` is a placeholder until GUI-2 builds the real sign-in flow (email/password, then the
2FA challenge).

## How it is served

The API host serves these files, so the portal is one IIS site and the session cookie is
same-origin. In Development the files are served straight from this folder, so
`npm run watch:css` shows up on refresh. On publish they are copied into the API's `wwwroot`
by the `AddFrontendToPublish` target in `Bitstream.Api.csproj`.

To include the Tailwind build in a publish (on a host with Node — normally the CI agent):

```bash
dotnet publish src/Bitstream.Api -c Release -p:BuildFrontend=true
```

Without that flag the publish uses whatever `wwwroot/css/app.css` is already on disk and warns
if there is none, so a backend-only build never needs Node installed.

## Tailwind v4 notes

Configuration is CSS-first: there is no `tailwind.config.js` and no PostCSS pipeline. Theme
tokens are declared in `@theme`, the files scanned for class names in `@source`, and shared
component classes in `@layer components`. A class that other classes need to `@apply` must be
declared with `@utility` rather than as a plain class — `.btn` is the one case of that here.

## Conventions worth keeping

- **Hiding a control is not access control.** The navigation renders what the session may
  use, but every call is authorised server-side regardless (TR-SEC-17).
- **Validation is duplicated, not delegated.** Client-side checks are for responsiveness;
  the server repeats all of them independently (TR-ACT-05).
- **Errors say what is wrong.** Field-level messages, never a generic failure string
  (TR-NFR-12).
- **Timestamps** arrive as UTC with offset and are rendered in local time (TR-DAT-08).
- **Labels are externalised** once the interface languages are agreed — TRD §11.4 open item
  11, currently unanswered, so no localisation framework has been chosen (TR-NFR-13).
- **Accessibility**: WCAG 2.1 AA for contrast, keyboard navigation and form labelling
  (TR-NFR-14). The theme colours in `app.css` were picked to meet the contrast ratio; check
  anything added the same way.

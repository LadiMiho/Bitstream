/**
 * Shared slide-out drawer: one component, filled with a different server-rendered partial each
 * time, used by every add/edit/view/change-password action across the admin screens rather than
 * a bespoke panel per screen. The drawer element itself (Pages/Shared/_Layout.cshtml) is shared
 * by every page, so any page can call openDrawer()/closeDrawer() without owning any drawer markup
 * of its own — only the fetched partial's fields differ.
 *
 * A partial defines its own footer buttons as a <template data-drawer-footer> sibling of its
 * <form> (see Views/Users/_AddDrawer.cshtml) — openDrawer() relocates that template's content
 * into the shared footer region once the partial is loaded, so the buttons stay declared next to
 * the form they submit while still rendering pinned to the bottom of the drawer. A submit button
 * moved this way needs a form="..." attribute (its own <form>'s id) since it is no longer a
 * descendant of that <form> once relocated — native form submission does not care where in the
 * document a button lives, only what its form attribute names.
 *
 * Page-specific behaviour (what a drawer's form actually submits to) stays in each page's own
 * script, wired through the exported drawerBody/drawerFooter elements — this module only owns
 * opening, closing, and the two behaviours every drawer needs regardless of what it contains:
 * relocating the footer template, and the password show/hide toggle.
 */

function el(selector) {
  return document.querySelector(selector);
}

const drawer = el('#drawer');
const drawerBackdrop = el('#drawer-backdrop');
const drawerTitle = el('#drawer-title');
export const drawerBody = el('#drawer-body');
export const drawerFooter = el('#drawer-footer');

/**
 * @param {string} title
 * @param {string} url Partial view URL fetched and injected into the drawer body.
 */
export async function openDrawer(title, url) {
  drawerTitle.textContent = title;
  drawerBody.innerHTML = '';
  drawerFooter.innerHTML = '';
  drawer.hidden = false;
  drawerBackdrop.hidden = false;
  drawer.setAttribute('aria-hidden', 'false');

  try {
    const response = await fetch(url, { credentials: 'same-origin' });

    if (!response.ok) {
      drawerBody.innerHTML = '<p class="text-sm text-ink-muted">This record is not available.</p>';
      return;
    }

    drawerBody.innerHTML = await response.text();

    const footerTemplate = drawerBody.querySelector('template[data-drawer-footer]');
    if (footerTemplate instanceof HTMLTemplateElement) {
      drawerFooter.replaceChildren(footerTemplate.content.cloneNode(true));
      footerTemplate.remove();
    }

    drawerBody.querySelector('input, select, textarea')?.focus();
  } catch {
    drawerBody.innerHTML = '<p class="text-sm text-ink-muted">Something went wrong loading this form.</p>';
  }
}

export function closeDrawer() {
  drawer.hidden = true;
  drawerBackdrop.hidden = true;
  drawer.setAttribute('aria-hidden', 'true');
  drawerBody.innerHTML = '';
  drawerFooter.innerHTML = '';
}

// --- Behaviour every drawer needs, regardless of what partial it's showing -------------------
el('#drawer-close').addEventListener('click', closeDrawer);
drawerBackdrop.addEventListener('click', closeDrawer);

// Delegated: a partial's Cancel button lives in the footer once relocated, and the footer is
// re-filled on every openDrawer() call, so a listener attached once here (rather than per-drawer)
// keeps working regardless of which partial is currently loaded.
document.addEventListener('click', (event) => {
  if (event.target.closest('[data-drawer-cancel]')) {
    closeDrawer();
  }
});

/**
 * Password show/hide: a partial adds a toggle button with data-password-toggle="<input id>"
 * next to the password field it controls (see Views/Users/_AddDrawer.cshtml) — no page-specific
 * script needs to wire this up itself.
 */
document.addEventListener('click', (event) => {
  const toggle = event.target.closest('[data-password-toggle]');
  if (!toggle) {
    return;
  }

  const input = document.getElementById(toggle.dataset.passwordToggle);
  if (!(input instanceof HTMLInputElement)) {
    return;
  }

  const isHidden = input.type === 'password';
  input.type = isHidden ? 'text' : 'password';
  toggle.textContent = isHidden ? 'Hide' : 'Show';
  toggle.setAttribute('aria-label', isHidden ? 'Hide password' : 'Show password');
});

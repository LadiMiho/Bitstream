/**
 * Header dropdowns (Views/Shared/_Layout.cshtml): the Activations and Access Management menu
 * groups and the account menu. Each [data-nav-menu] has one [data-nav-trigger] button and one
 * [data-nav-panel]. Only one panel is open at a time; a click outside, Escape, or opening another
 * closes it. Presentation only — every link is an ordinary page navigation.
 */
const menus = [...document.querySelectorAll('[data-nav-menu]')];

function setOpen(menu, open) {
  const trigger = menu.querySelector('[data-nav-trigger]');
  const panel = menu.querySelector('[data-nav-panel]');
  if (!trigger || !panel) {
    return;
  }
  panel.hidden = !open;
  trigger.setAttribute('aria-expanded', String(open));
}

function closeAll(except) {
  menus.filter((menu) => menu !== except).forEach((menu) => setOpen(menu, false));
}

for (const menu of menus) {
  const trigger = menu.querySelector('[data-nav-trigger]');
  trigger?.addEventListener('click', (event) => {
    event.stopPropagation();
    const open = trigger.getAttribute('aria-expanded') !== 'true';
    closeAll(menu);
    setOpen(menu, open);
    if (open) {
      menu.querySelector('[data-nav-panel] a, [data-nav-panel] button')?.focus();
    }
  });

  menu.querySelector('[data-nav-panel]')?.addEventListener('click', (event) => event.stopPropagation());
}

document.addEventListener('click', () => closeAll(null));

document.addEventListener('keydown', (event) => {
  if (event.key !== 'Escape') {
    return;
  }
  const open = menus.find((menu) => menu.querySelector('[data-nav-trigger]')?.getAttribute('aria-expanded') === 'true');
  if (open) {
    setOpen(open, false);
    open.querySelector('[data-nav-trigger]')?.focus();
  }
});

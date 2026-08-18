/**
 * Shared markup for a module page that has no screen yet. Replaced view by view as each
 * module is built; nothing else references this once the last placeholder is gone.
 */

/**
 * @param {HTMLElement} container
 * @param {{ title: string, description: string }} content
 */
export function renderPlaceholder(container, { title, description }) {
  container.innerHTML = `
    <section class="card" aria-labelledby="page-heading">
      <h1 id="page-heading" class="text-xl font-semibold">${title}</h1>
      <p class="mt-2 max-w-2xl text-sm text-ink-muted">${description}</p>
    </section>
  `;
}

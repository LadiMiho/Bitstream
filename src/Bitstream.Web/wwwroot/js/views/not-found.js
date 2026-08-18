import { renderPlaceholder } from './placeholder.js';

/** @param {HTMLElement} container */
export default function renderNotFound(container) {
  renderPlaceholder(container, {
    title: 'Page not found',
    description: 'There is no page at this address. Use the navigation above to find your way back.'
  });
}

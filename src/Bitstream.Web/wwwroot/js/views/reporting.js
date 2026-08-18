import { renderPlaceholder } from './placeholder.js';

/** @param {HTMLElement} container */
export default function renderReporting(container) {
  renderPlaceholder(container, {
    title: 'Reporting',
    description:
      'Operational and management reporting will be built here — this page is a ' +
      'placeholder reachable from the navigation until then.'
  });
}

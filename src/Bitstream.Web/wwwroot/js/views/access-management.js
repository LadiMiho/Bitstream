import { renderPlaceholder } from './placeholder.js';

/** @param {HTMLElement} container */
export default function renderAccessManagement(container) {
  renderPlaceholder(container, {
    title: 'Access Management',
    description:
      'User, ISP and role administration (TRD §4) will be built here — this page is a ' +
      'placeholder reachable from the navigation until then.'
  });
}

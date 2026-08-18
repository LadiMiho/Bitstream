import { renderPlaceholder } from './placeholder.js';

/** @param {HTMLElement} container */
export default function renderPostActivation(container) {
  renderPlaceholder(container, {
    title: 'Post-Activation Support',
    description:
      'Complaint tickets, the closure handshake, auto-confirmation and service status ' +
      'changes (TRD §6) will be built here — this page is a placeholder reachable from the ' +
      'navigation until then.'
  });
}

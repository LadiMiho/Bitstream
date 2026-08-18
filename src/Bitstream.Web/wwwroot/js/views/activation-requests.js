import { renderPlaceholder } from './placeholder.js';

/** @param {HTMLElement} container */
export default function renderActivationRequests(container) {
  renderPlaceholder(container, {
    title: 'Activation Requests',
    description:
      'Submission, GIS verification and CRM-driven progress for activation requests ' +
      '(TRD §5) will be built here — this page is a placeholder reachable from the ' +
      'navigation until then.'
  });
}

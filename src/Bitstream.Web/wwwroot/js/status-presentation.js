/**
 * Presentation only: maps an activation request's `status` field (TRD 5.3,
 * Bitstream.Domain.Enums.ActivationRequestStatus, returned verbatim by the API) to a label and
 * a status-pill colour. The set of statuses and which one a request is in are decided entirely
 * server-side; this file does not decide anything, it only chooses how to display a value the
 * API already gave.
 */

const STATUS_PRESENTATION = {
  Submitted: { label: 'Submitted', tone: 'progress' },
  PendingCrmSync: { label: 'Pending CRM Sync', tone: 'pending' },
  AwaitingGisVerification: { label: 'Awaiting GIS Verification', tone: 'progress' },
  RejectedNoLine: { label: 'Rejected — No Line', tone: 'blocked' },
  LineAvailable: { label: 'Line Available', tone: 'progress' },
  SalesOrderOpened: { label: 'Sales Order Opened', tone: 'progress' },
  InProvisioning: { label: 'In Provisioning', tone: 'progress' },
  Closed: { label: 'Closed', tone: 'blocked' },
  Completed: { label: 'Completed', tone: 'done' },
  IntegrationFailed: { label: 'Integration Failed', tone: 'blocked' }
};

const TONE_CLASSES = {
  pending: 'bg-state-pending/15 text-state-pending',
  progress: 'bg-state-progress/15 text-state-progress',
  done: 'bg-state-done/15 text-state-done',
  blocked: 'bg-state-blocked/15 text-state-blocked'
};

/** @param {string} status @returns {{ label: string, className: string }} */
export function presentStatus(status) {
  const entry = STATUS_PRESENTATION[status] || { label: status, tone: 'progress' };
  return { label: entry.label, className: `status-pill ${TONE_CLASSES[entry.tone]}` };
}

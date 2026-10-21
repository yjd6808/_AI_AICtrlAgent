import type { SessionDisplayState } from '../../api/types'

export type SessionActions = {
  send: boolean
  abort: boolean
  abortAndSend: boolean
}

export function getSessionActions(
  displayState: SessionDisplayState | undefined,
  managed: boolean,
): SessionActions {
  if (!managed || !displayState) {
    return { send: false, abort: false, abortAndSend: false }
  }

  switch (displayState) {
    case 'Idle':
    case 'Errored':
    case 'Aborted':
    case 'QuotaBlocked':
      return { send: true, abort: false, abortAndSend: false }
    case 'Running':
    case 'Retrying':
    case 'Stalled':
    case 'WaitingPermission':
      return { send: false, abort: true, abortAndSend: true }
    case 'ForeignBusy':
    case 'Unknown':
    default:
      return { send: false, abort: false, abortAndSend: false }
  }
}

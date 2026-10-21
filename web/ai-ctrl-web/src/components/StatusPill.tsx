import type { SessionDisplayState } from '../api/types'
import { displayStateDescriptions, displayStateLabels } from '../api/labels'
import { Badge, type BadgeTone } from './Badge'
import styles from './StatusPill.module.css'

const tones: Record<SessionDisplayState, BadgeTone> = {
  Running: 'info',
  Retrying: 'info',
  WaitingPermission: 'warn',
  Stalled: 'warn',
  QuotaBlocked: 'danger',
  Errored: 'danger',
  ForeignBusy: 'purple',
  Aborted: 'neutral',
  Idle: 'ok',
  Unknown: 'neutral',
}

export function StatusPill({ state }: { state: SessionDisplayState }) {
  return (
    <Badge tone={tones[state]} title={displayStateDescriptions[state]}>
      <span aria-hidden="true" className={styles.dot} />
      {displayStateLabels[state] ?? state}
    </Badge>
  )
}

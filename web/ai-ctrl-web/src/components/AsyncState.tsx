import type { ReactNode } from 'react'
import { AlertTriangle, Inbox, LoaderCircle } from 'lucide-react'
import { describeApiError } from '../api/errors'
import { Button } from './Button'
import styles from './AsyncState.module.css'

export function LoadingBlock({ label = '불러오는 중…' }: { label?: string }) {
  return (
    <div className={styles.state} role="status" aria-live="polite">
      <LoaderCircle className={styles.spin} size={22} aria-hidden="true" />
      <p className={styles.message}>{label}</p>
    </div>
  )
}

export function EmptyBlock({
  title,
  description,
  action,
}: {
  title: string
  description?: string
  action?: ReactNode
}) {
  return (
    <div className={styles.state}>
      <Inbox className={styles.icon} size={26} aria-hidden="true" />
      <p className={styles.title}>{title}</p>
      {description ? <p className={styles.message}>{description}</p> : null}
      {action}
    </div>
  )
}

export function ErrorBlock({
  error,
  title,
  onRetry,
  compact = false,
}: {
  error: unknown
  title?: string
  onRetry?: () => void
  compact?: boolean
}) {
  const description = describeApiError(error)
  return (
    <div className={compact ? styles.errorCompact : styles.error} role="alert">
      <div className={styles.errorHeader}>
        <AlertTriangle size={18} aria-hidden="true" />
        <p className={styles.title}>{title ?? description.title}</p>
      </div>
      <p className={styles.message}>{description.message}</p>
      {description.hint ? <p className={styles.hint}>{description.hint}</p> : null}
      {description.traceId ? (
        <code className={styles.trace}>trace: {description.traceId}</code>
      ) : null}
      {onRetry ? (
        <div className={styles.action}>
          <Button size="sm" tone="secondary" onClick={onRetry}>
            다시 시도
          </Button>
        </div>
      ) : null}
    </div>
  )
}

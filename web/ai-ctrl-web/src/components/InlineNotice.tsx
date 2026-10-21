import type { ReactNode } from 'react'
import { AlertTriangle, CheckCircle2, Info, ShieldAlert } from 'lucide-react'
import { cx } from '../lib/cx'
import styles from './InlineNotice.module.css'

type NoticeTone = 'info' | 'warn' | 'danger' | 'ok'

const icons = {
  info: Info,
  warn: AlertTriangle,
  danger: ShieldAlert,
  ok: CheckCircle2,
}

export function InlineNotice({
  tone = 'info',
  title,
  children,
  action,
}: {
  tone?: NoticeTone
  title?: string
  children: ReactNode
  action?: ReactNode
}) {
  const Icon = icons[tone]
  return (
    <div className={cx(styles.notice, styles[tone])} role={tone === 'danger' ? 'alert' : 'status'}>
      <Icon size={17} className={styles.icon} aria-hidden="true" />
      <div className={styles.body}>
        {title ? <p className={styles.title}>{title}</p> : null}
        <div className={styles.message}>{children}</div>
      </div>
      {action ? <div className={styles.action}>{action}</div> : null}
    </div>
  )
}

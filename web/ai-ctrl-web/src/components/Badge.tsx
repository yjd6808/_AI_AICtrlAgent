import type { ReactNode } from 'react'
import { cx } from '../lib/cx'
import styles from './Badge.module.css'

export type BadgeTone = 'neutral' | 'ok' | 'warn' | 'danger' | 'info' | 'purple' | 'accent'

type BadgeProps = {
  tone?: BadgeTone
  children: ReactNode
  title?: string
  className?: string
}

export function Badge({ tone = 'neutral', children, title, className }: BadgeProps) {
  return (
    <span className={cx(styles.badge, styles[tone], className)} title={title}>
      {children}
    </span>
  )
}

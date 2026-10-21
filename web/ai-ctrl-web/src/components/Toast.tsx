import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react'
import { AlertTriangle, CheckCircle2, Info, X } from 'lucide-react'
import { cx } from '../lib/cx'
import styles from './Toast.module.css'

export type ToastKind = 'info' | 'success' | 'error'

type Toast = {
  id: string
  kind: ToastKind
  message: string
}

type ToastContextValue = {
  notify: (kind: ToastKind, message: string) => void
}

const ToastContext = createContext<ToastContextValue | null>(null)

const icons = {
  info: Info,
  success: CheckCircle2,
  error: AlertTriangle,
}

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([])
  const timers = useRef(new Map<string, number>())

  const dismiss = useCallback((id: string) => {
    setToasts((current) => current.filter((toast) => toast.id !== id))
    const timer = timers.current.get(id)
    if (timer !== undefined) {
      window.clearTimeout(timer)
      timers.current.delete(id)
    }
  }, [])

  const notify = useCallback(
    (kind: ToastKind, message: string) => {
      const id = `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`
      setToasts((current) => [...current.slice(-3), { id, kind, message }])
      const timer = window.setTimeout(() => dismiss(id), kind === 'error' ? 8_000 : 4_500)
      timers.current.set(id, timer)
    },
    [dismiss],
  )

  useEffect(() => {
    const activeTimers = timers.current
    return () => {
      for (const timer of activeTimers.values()) {
        window.clearTimeout(timer)
      }
      activeTimers.clear()
    }
  }, [])

  const value = useMemo(() => ({ notify }), [notify])

  return (
    <ToastContext.Provider value={value}>
      {children}
      <div className={styles.viewport} aria-live="polite" aria-atomic="false">
        {toasts.map((toast) => {
          const Icon = icons[toast.kind]
          return (
            <div key={toast.id} className={cx(styles.toast, styles[toast.kind])} role="status">
              <Icon size={17} aria-hidden="true" />
              <p className={styles.message}>{toast.message}</p>
              <button
                type="button"
                className={styles.close}
                aria-label="알림 닫기"
                onClick={() => dismiss(toast.id)}
              >
                <X size={15} aria-hidden="true" />
              </button>
            </div>
          )
        })}
      </div>
    </ToastContext.Provider>
  )
}

export function useToast(): ToastContextValue {
  const context = useContext(ToastContext)
  if (!context) {
    throw new Error('useToast must be used inside ToastProvider')
  }
  return context
}

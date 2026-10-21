import { Navigate, Outlet, useLocation } from 'react-router-dom'
import { LoaderCircle, ShieldCheck } from 'lucide-react'
import { useMe } from '../api/queries'
import { isUnauthorized } from '../api/errors'
import { ErrorBlock } from '../components/AsyncState'
import styles from './AuthGate.module.css'

export function AuthGuard() {
  const me = useMe()
  const location = useLocation()

  if (me.isLoading) {
    return (
      <div className={styles.page}>
        <div className={styles.brand}>
          <span className={styles.mark}>
            <ShieldCheck size={22} aria-hidden="true" />
          </span>
          <p className={styles.title}>AI Ctrl Agent</p>
          <LoaderCircle className={styles.spin} size={18} aria-hidden="true" />
          <p className={styles.message}>세션을 확인하는 중…</p>
        </div>
      </div>
    )
  }

  if (me.isError) {
    if (isUnauthorized(me.error)) {
      return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />
    }
    return (
      <div className={styles.page}>
        <div className={styles.errorCard}>
          <ErrorBlock error={me.error} onRetry={() => void me.refetch()} />
        </div>
      </div>
    )
  }

  return <Outlet />
}

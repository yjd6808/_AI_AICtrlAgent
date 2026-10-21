import { Navigate, Outlet, useLocation } from 'react-router-dom'
import { ArrowLeftRight, LockKeyhole } from 'lucide-react'
import { useMe } from '../api/queries'
import { loginUrl } from '../api/client'
import { ErrorBlock } from '../components/AsyncState'
import { isUnauthorized } from '../api/errors'
import styles from './LoginPage.module.css'

type LoginState = { from?: string } | null

export function LoginPage() {
  const me = useMe()
  const location = useLocation()
  const state = (location.state ?? null) as LoginState
  const returnTo = state?.from && state.from.startsWith('/') ? state.from : '/'

  if (me.data) {
    return <Navigate to={returnTo} replace />
  }

  const networkError = me.isError && !isUnauthorized(me.error)

  return (
    <main className={styles.page}>
      <section className={styles.card}>
        <div className={styles.icon} aria-hidden="true">
          <LockKeyhole size={24} />
        </div>
        <p className={styles.eyebrow}>AI Ctrl Agent</p>
        <h1 className={styles.title}>원격 제어 콘솔</h1>
        <p className={styles.description}>
          등록된 Google 계정으로 로그인하면 연결된 머신과 AI 세션을 확인할 수 있습니다.
        </p>

        {networkError ? (
          <ErrorBlock error={me.error} compact onRetry={() => void me.refetch()} />
        ) : null}

        <a className={styles.loginButton} href={loginUrl(returnTo)}>
          <ArrowLeftRight size={17} aria-hidden="true" />
          Google로 로그인
        </a>

        <p className={styles.notice}>
          이 브라우저에는 인증 토큰을 저장하지 않습니다. 로그인 상태는 서버의 보안 쿠키로
          유지됩니다.
        </p>
      </section>
      <p className={styles.footer}>Self-hosted remote operations for AI coding agents</p>
      <Outlet />
    </main>
  )
}

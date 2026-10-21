import { Component, type ErrorInfo, type ReactNode } from 'react'
import styles from './ErrorBoundary.module.css'

type Props = { children: ReactNode }
type State = { error: Error | null }

export class ErrorBoundary extends Component<Props, State> {
  override state: State = { error: null }

  static getDerivedStateFromError(error: Error): State {
    return { error }
  }

  override componentDidCatch(error: Error, info: ErrorInfo) {
    if (import.meta.env.DEV) {
      console.error('Render error', error, info.componentStack)
    }
  }

  override render() {
    if (this.state.error) {
      return (
        <main className={styles.page}>
          <div className={styles.card}>
            <p className={styles.kicker}>렌더링 오류</p>
            <h1 className={styles.title}>화면을 표시할 수 없습니다</h1>
            <p className={styles.message}>
              예상하지 못한 오류가 발생했습니다. 페이지를 새로고침한 뒤 다시 시도하세요.
            </p>
            <code className={styles.detail}>{this.state.error.message}</code>
            <button
              type="button"
              className={styles.button}
              onClick={() => window.location.reload()}
            >
              페이지 새로고침
            </button>
          </div>
        </main>
      )
    }
    return this.props.children
  }
}

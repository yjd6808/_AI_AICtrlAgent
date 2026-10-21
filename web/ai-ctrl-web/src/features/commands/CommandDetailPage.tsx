import { Link, useParams } from 'react-router-dom'
import { ArrowLeft, RefreshCw } from 'lucide-react'
import type { CommandStatus } from '../../api/types'
import { useCommandDetail, isTerminalCommandStatus } from '../../api/queries'
import { commandStatusLabels } from '../../api/labels'
import { formatAbsolute, formatRelative } from '../../api/format'
import { Badge, type BadgeTone } from '../../components/Badge'
import { EmptyBlock, ErrorBlock, LoadingBlock } from '../../components/AsyncState'
import { FreshnessBadge } from '../../components/FreshnessBadge'
import { InlineNotice } from '../../components/InlineNotice'
import { PageHeader } from '../../components/PageHeader'
import styles from './CommandDetailPage.module.css'

const statusTones: Record<CommandStatus, BadgeTone> = {
  Created: 'neutral',
  Queued: 'neutral',
  Dispatched: 'info',
  Acknowledged: 'info',
  Running: 'info',
  Succeeded: 'ok',
  Failed: 'danger',
  Cancelled: 'neutral',
  Expired: 'warn',
  Unknown: 'neutral',
}

function stringifyResult(value: unknown): string {
  if (value === null || value === undefined) {
    return ''
  }
  try {
    return JSON.stringify(value, null, 2) ?? String(value)
  } catch {
    return String(value)
  }
}

export function CommandDetailPage() {
  const { commandId } = useParams<{ commandId: string }>()
  const command = useCommandDetail(commandId)

  if (!commandId) {
    return <EmptyBlock title="명령 ID가 없습니다" />
  }

  if (command.isLoading) {
    return <LoadingBlock label="명령 상세를 불러오는 중…" />
  }

  if (command.isError) {
    return (
      <div className={styles.page}>
        <ErrorBlock error={command.error} onRetry={() => void command.refetch()} />
        <Link to="/commands" className={styles.backLink}>
          <ArrowLeft size={14} aria-hidden="true" />
          명령 이력
        </Link>
      </div>
    )
  }

  if (!command.data) {
    return <EmptyBlock title="명령을 찾을 수 없습니다" />
  }

  const detail = command.data.data
  const terminal = isTerminalCommandStatus(detail.status)
  const resultText = stringifyResult(detail.result)

  return (
    <div className={styles.page}>
      <PageHeader
        eyebrow="Command"
        title={detail.type}
        description={detail.commandId}
        meta={
          <>
            <Badge tone={statusTones[detail.status]}>
              {commandStatusLabels[detail.status]}
            </Badge>
            <FreshnessBadge meta={command.data.meta} />
          </>
        }
        actions={
          <>
            <Link to="/commands" className={styles.backLink}>
              <ArrowLeft size={14} aria-hidden="true" />
              명령 이력
            </Link>
            <button
              type="button"
              className={styles.refreshButton}
              onClick={() => void command.refetch()}
              aria-label="명령 새로고침"
            >
              <RefreshCw size={14} aria-hidden="true" />
            </button>
          </>
        }
      />

      {!terminal ? (
        <InlineNotice tone="info" title="명령 진행 중">
          최종 상태가 확인될 때까지 1초마다 자동으로 갱신합니다.
        </InlineNotice>
      ) : null}

      {detail.errorMessage ? (
        <InlineNotice tone="danger" title={detail.errorCode ?? '명령 오류'}>
          {detail.errorMessage}
        </InlineNotice>
      ) : null}

      <div className={styles.summary}>
        <div>
          <p className={styles.label}>발행자</p>
          <p className={styles.value}>{detail.issuedBy}</p>
        </div>
        <div>
          <p className={styles.label}>발행 시각</p>
          <p className={styles.value} title={formatAbsolute(detail.issuedAt)}>
            {formatAbsolute(detail.issuedAt)}
          </p>
        </div>
        <div>
          <p className={styles.label}>업데이트</p>
          <p className={styles.value} title={formatAbsolute(detail.updatedAt)}>
            {formatRelative(detail.updatedAt)}
          </p>
        </div>
      </div>

      <section className={styles.panel}>
        <h2 className={styles.panelTitle}>단계</h2>
        {detail.steps.length === 0 ? (
          <p className={styles.empty}>기록된 단계가 없습니다.</p>
        ) : (
          <ol className={styles.steps}>
            {detail.steps.map((step) => (
              <li key={`${step.seq}-${step.at}`} className={styles.step}>
                <span className={styles.seq}>{step.seq}</span>
                <div className={styles.stepBody}>
                  <p className={styles.stepName}>{step.step}</p>
                  {step.message ? <p className={styles.stepMessage}>{step.message}</p> : null}
                  <time className={styles.stepTime} dateTime={step.at}>
                    {formatAbsolute(step.at)}
                  </time>
                </div>
              </li>
            ))}
          </ol>
        )}
      </section>

      <section className={styles.panel}>
        <h2 className={styles.panelTitle}>결과</h2>
        {resultText ? (
          <pre className={styles.code}>
            <code>{resultText}</code>
          </pre>
        ) : (
          <p className={styles.empty}>결과 데이터가 없습니다.</p>
        )}
      </section>
    </div>
  )
}

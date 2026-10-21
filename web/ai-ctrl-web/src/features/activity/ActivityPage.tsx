import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { RefreshCw } from 'lucide-react'
import { useAudit } from '../../api/queries'
import type { AuditFilters } from '../../api/types'
import { formatAbsolute, formatRelative } from '../../api/format'
import { Badge, type BadgeTone } from '../../components/Badge'
import { Button } from '../../components/Button'
import { EmptyBlock, ErrorBlock, LoadingBlock } from '../../components/AsyncState'
import { FreshnessBadge } from '../../components/FreshnessBadge'
import { PageHeader } from '../../components/PageHeader'
import styles from './ActivityPage.module.css'

type RangeKey = '1h' | '24h' | '7d'

const rangeMs: Record<RangeKey, number> = {
  '1h': 60 * 60 * 1000,
  '24h': 24 * 60 * 60 * 1000,
  '7d': 7 * 24 * 60 * 60 * 1000,
}

function resultTone(result: string): BadgeTone {
  const value = result.toLowerCase()
  if (['accepted', 'success', 'succeeded', 'ok', 'completed'].includes(value)) {
    return 'ok'
  }
  if (['failed', 'denied', 'error', 'rejected'].includes(value)) {
    return 'danger'
  }
  if (['pending', 'running', 'queued'].includes(value)) {
    return 'info'
  }
  return 'neutral'
}

export function ActivityPage() {
  const [range, setRange] = useState<RangeKey>('24h')
  const from = useMemo(
    () => new Date(Date.now() - rangeMs[range]).toISOString(),
    [range],
  )
  const filters: AuditFilters = useMemo(() => ({ from, limit: 200 }), [from])
  const audit = useAudit(filters, true, 5_000)
  const events = audit.data?.data ?? []

  return (
    <div className={styles.page}>
      <PageHeader
        eyebrow="Activity"
        title="활동 로그"
        description="명령 발행, 완료, 머신 등록과 같은 감사 이벤트를 확인합니다."
        meta={audit.data ? <FreshnessBadge meta={audit.data.meta} /> : null}
        actions={
          <>
            <select
              className={styles.rangeSelect}
              value={range}
              onChange={(event) => setRange(event.target.value as RangeKey)}
              aria-label="조회 기간"
            >
              <option value="1h">최근 1시간</option>
              <option value="24h">최근 24시간</option>
              <option value="7d">최근 7일</option>
            </select>
            <Button
              tone="secondary"
              size="sm"
              icon={<RefreshCw size={14} aria-hidden="true" />}
              loading={audit.isFetching && !audit.isLoading}
              onClick={() => void audit.refetch()}
            >
              새로고침
            </Button>
          </>
        }
      />

      {audit.isLoading ? (
        <LoadingBlock label="감사 로그를 불러오는 중…" />
      ) : audit.isError ? (
        <ErrorBlock error={audit.error} onRetry={() => void audit.refetch()} />
      ) : events.length === 0 ? (
        <EmptyBlock
          title="감사 이벤트가 없습니다"
          description="선택한 기간에 기록된 활동이 없습니다."
        />
      ) : (
        <ol className={styles.list}>
          {events.map((event) => (
            <li key={event.id} className={styles.item}>
              <div className={styles.itemHeader}>
                <time
                  className={styles.time}
                  dateTime={event.at}
                  title={formatAbsolute(event.at)}
                >
                  {formatRelative(event.at)}
                </time>
                <Badge tone={resultTone(event.result)}>{event.result}</Badge>
              </div>
              <p className={styles.action}>{event.action}</p>
              <div className={styles.meta}>
                <span>{event.actor}</span>
                {event.ip ? (
                  <>
                    <span aria-hidden="true">·</span>
                    <span>{event.ip}</span>
                  </>
                ) : null}
                {event.country ? (
                  <>
                    <span aria-hidden="true">·</span>
                    <span>{event.country}</span>
                  </>
                ) : null}
              </div>
              {event.target ? <p className={styles.target}>대상: {event.target}</p> : null}
              {event.commandId ? (
                <Link
                  to={`/commands/${encodeURIComponent(event.commandId)}`}
                  className={styles.commandLink}
                >
                  명령 {event.commandId}
                </Link>
              ) : null}
            </li>
          ))}
        </ol>
      )}
    </div>
  )
}

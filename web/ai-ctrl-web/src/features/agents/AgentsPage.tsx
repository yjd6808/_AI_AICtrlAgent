import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { ChevronRight, FolderTree, RefreshCw, Server } from 'lucide-react'
import {
  useInstances,
  useMachine,
  useRefreshInstances,
  useSessions,
} from '../../api/queries'
import { describeApiError } from '../../api/errors'
import { modeLabels, providerLabels } from '../../api/labels'
import type { InstanceInfo, SessionSummary } from '../../api/types'
import { formatElapsed, formatRelative } from '../../api/format'
import { buildSessionRows, sortSessions } from './sessionOrder'
import { Badge } from '../../components/Badge'
import { Button } from '../../components/Button'
import { FreshnessBadge } from '../../components/FreshnessBadge'
import { InlineNotice } from '../../components/InlineNotice'
import { PageHeader } from '../../components/PageHeader'
import { StatusPill } from '../../components/StatusPill'
import { EmptyBlock, ErrorBlock, LoadingBlock } from '../../components/AsyncState'
import { useToast } from '../../components/Toast'
import { instanceLabel } from '../../app/featureRoutes'
import { cx } from '../../lib/cx'
import styles from './AgentsPage.module.css'

export function AgentsPage() {
  const { machineId, instanceId } = useParams<{
    machineId: string
    instanceId: string
  }>()
  const instances = useInstances(machineId)

  if (!machineId) {
    return <EmptyBlock title="머신을 선택하세요" description="사이드바에서 머신을 선택합니다." />
  }

  if (instances.isLoading) {
    return <LoadingBlock label="AI 에이전트를 불러오는 중…" />
  }

  if (instances.isError) {
    return <ErrorBlock error={instances.error} onRetry={() => void instances.refetch()} />
  }

  const list = instances.data?.data ?? []

  if (instanceId) {
    const instance = list.find((item) => item.id.value === instanceId)
    if (!instance) {
      return (
        <EmptyBlock
          title="인스턴스를 찾을 수 없습니다"
          description="OpenCode 또는 Claude Code 인스턴스가 종료되었거나 감지되지 않았습니다."
          action={
            <Link to={`/m/${encodeURIComponent(machineId)}`} className={styles.textLink}>
              에이전트 목록으로 돌아가기
            </Link>
          }
        />
      )
    }
    return <InstanceAgents machineId={machineId} instance={instance} />
  }

  return <AgentsOverview machineId={machineId} instances={list} />
}

function AgentsOverview({
  machineId,
  instances,
}: {
  machineId: string
  instances: InstanceInfo[]
}) {
  const toast = useToast()
  const machine = useMachine(machineId)
  const refresh = useRefreshInstances(machineId)

  const onRefresh = () => {
    refresh.mutate(undefined, {
      onSuccess: (result) => {
        toast.notify(
          'success',
          result.data.kind === 'pending'
            ? '인스턴스 새로고침 명령을 접수했습니다.'
            : '인스턴스 목록을 새로고침했습니다.',
        )
      },
      onError: (error) => {
        toast.notify('error', describeApiError(error).message)
      },
    })
  }

  return (
    <div className={styles.page}>
      <PageHeader
        eyebrow="Agents"
        title="AI Agents"
        description="연결된 OpenCode와 Claude Code 인스턴스의 활성 세션을 확인합니다."
        meta={machine.data ? <FreshnessBadge meta={machine.data.meta} /> : null}
        actions={
          <Button
            tone="secondary"
            size="sm"
            icon={<RefreshCw size={15} aria-hidden="true" />}
            loading={refresh.isPending}
            onClick={onRefresh}
          >
            인스턴스 새로고침
          </Button>
        }
      />

      {machine.data && !machine.data.data.online ? (
        <InlineNotice tone="warn" title="머신 오프라인">
          캐시된 세션 정보만 표시할 수 있습니다. Client 연결을 확인한 뒤 새로고침하세요.
        </InlineNotice>
      ) : null}

      {instances.length === 0 ? (
        <EmptyBlock
          title="감지된 인스턴스가 없습니다"
          description="머신에서 OpenCode 또는 Claude Code를 실행한 뒤 인스턴스 목록을 새로고침하세요."
          action={
            <Button
              tone="secondary"
              size="sm"
              loading={refresh.isPending}
              onClick={onRefresh}
            >
              새로고침
            </Button>
          }
        />
      ) : (
        <div className={styles.instanceGrid}>
          {instances.map((instance) => (
            <InstanceCard
              key={instance.id.value}
              machineId={machineId}
              instance={instance}
            />
          ))}
        </div>
      )}

      <InlineNotice tone="info" title="세션 생성은 아직 웹에서 지원하지 않습니다">
        M4 API에는 <code>session.create</code>가 없습니다. 새 세션은 Desktop에서 시작한 뒤 이
        화면에서 이어서 제어하세요.
      </InlineNotice>
    </div>
  )
}

function InstanceCard({
  machineId,
  instance,
}: {
  machineId: string
  instance: InstanceInfo
}) {
  const sessions = useSessions(machineId, instance.id.value, 'active')
  const list = sessions.data?.data.sessions ?? []
  const sorted = sortSessions(list)
  const top = sorted[0]

  return (
    <Link
      to={`/m/${encodeURIComponent(machineId)}/agents/${encodeURIComponent(instance.id.value)}`}
      className={styles.instanceCard}
    >
      <div className={styles.cardHeader}>
        <span className={styles.providerIcon} aria-hidden="true">
          <Server size={17} />
        </span>
        <div className={styles.cardTitleBlock}>
          <p className={styles.cardTitle}>{instanceLabel(instance)}</p>
          <p className={styles.cardEndpoint}>{instance.endpoint}</p>
        </div>
        <ChevronRight size={17} className={styles.cardChevron} aria-hidden="true" />
      </div>
      <div className={styles.cardBadges}>
        <Badge tone={instance.healthy ? 'ok' : 'danger'}>
          {instance.healthy ? '정상' : '응답 없음'}
        </Badge>
        <Badge tone={instance.managed ? 'accent' : 'neutral'}>
          {modeLabels[instance.mode] ?? instance.mode}
        </Badge>
        {!instance.managed ? <Badge tone="warn">읽기 전용</Badge> : null}
      </div>
      <div className={styles.cardStats}>
        <div>
          <p className={styles.statValue}>{list.length}</p>
          <p className={styles.statLabel}>활성 세션</p>
        </div>
        <div>
          <p className={styles.statValue}>{instance.version ?? '—'}</p>
          <p className={styles.statLabel}>버전</p>
        </div>
      </div>
      {sessions.isLoading ? (
        <p className={styles.cardHint}>세션 불러오는 중…</p>
      ) : sessions.isError ? (
        <p className={styles.cardError}>세션을 불러오지 못했습니다.</p>
      ) : top ? (
        <p className={styles.cardHint}>
          <StatusPill state={top.displayState} /> {top.title || '(제목 없음)'}
        </p>
      ) : (
        <p className={styles.cardHint}>활성 세션이 없습니다.</p>
      )}
    </Link>
  )
}

function InstanceAgents({
  machineId,
  instance,
}: {
  machineId: string
  instance: InstanceInfo
}) {
  const toast = useToast()
  const machine = useMachine(machineId)
  const sessions = useSessions(machineId, instance.id.value, 'active')
  const refresh = useRefreshInstances(machineId)
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set())

  const rows = buildSessionRows(sessions.data?.data.sessions ?? [], expanded)
  const managed = instance.managed

  const onRefresh = () => {
    refresh.mutate(undefined, {
      onSuccess: (result) => {
        toast.notify(
          'success',
          result.data.kind === 'pending'
            ? '새로고침 명령을 접수했습니다.'
            : '인스턴스 정보를 새로고침했습니다.',
        )
      },
      onError: (error) => {
        toast.notify('error', describeApiError(error).message)
      },
    })
  }

  const toggle = (sessionId: string) => {
    setExpanded((current) => {
      const next = new Set(current)
      if (next.has(sessionId)) {
        next.delete(sessionId)
      } else {
        next.add(sessionId)
      }
      return next
    })
  }

  return (
    <div className={styles.page}>
      <PageHeader
        eyebrow={providerLabels[instance.kind] ?? instance.kind}
        title={instanceLabel(instance)}
        description={instance.endpoint}
        meta={
          <>
            {sessions.data ? <FreshnessBadge meta={sessions.data.meta} /> : null}
            <Badge tone={instance.healthy ? 'ok' : 'danger'}>
              {instance.healthy ? '정상' : '응답 없음'}
            </Badge>
            {!managed ? <Badge tone="warn">읽기 전용</Badge> : null}
          </>
        }
        actions={
          <>
            <Link to={`/m/${encodeURIComponent(machineId)}`} className={styles.backLink}>
              에이전트 목록
            </Link>
            <Button
              tone="secondary"
              size="sm"
              icon={<RefreshCw size={15} aria-hidden="true" />}
              loading={refresh.isPending}
              onClick={onRefresh}
            >
              새로고침
            </Button>
          </>
        }
      />

      {!managed ? (
        <InlineNotice tone="warn" title="SharedStore 인스턴스">
          이 인스턴스는 다른 앱과 저장소를 공유하므로 웹에서는 읽기 전용입니다. 프롬프트 전송과
          중단은 비활성화됩니다.
        </InlineNotice>
      ) : null}

      {machine.data && !machine.data.data.online ? (
        <InlineNotice tone="warn" title="머신 오프라인">
          캐시된 세션 목록입니다. 실시간 명령은 머신이 다시 연결될 때까지 실패할 수 있습니다.
        </InlineNotice>
      ) : null}

      <section className={styles.sessionsPanel} aria-labelledby="active-sessions-title">
        <div className={styles.panelHeader}>
          <h2 id="active-sessions-title" className={styles.panelTitle}>
            활성 세션
          </h2>
          <span className={styles.panelCount}>{rows.length}</span>
        </div>

        {sessions.isLoading ? (
          <LoadingBlock label="세션을 불러오는 중…" />
        ) : sessions.isError ? (
          <ErrorBlock error={sessions.error} onRetry={() => void sessions.refetch()} />
        ) : rows.length === 0 ? (
          <EmptyBlock
            title="활성 세션이 없습니다"
            description="Desktop에서 세션을 시작하면 이 화면에 표시됩니다."
          />
        ) : (
          <div className={styles.sessionList}>
            {rows.map((row) => (
              <SessionRow
                key={row.session.ref.sessionId}
                machineId={machineId}
                instanceId={instance.id.value}
                session={row.session}
                depth={row.depth}
                hasChildren={row.hasChildren}
                expanded={row.expanded}
                onToggle={() => toggle(row.session.ref.sessionId)}
                readOnly={!managed}
              />
            ))}
          </div>
        )}
      </section>
    </div>
  )
}

function SessionRow({
  machineId,
  instanceId,
  session,
  depth,
  hasChildren,
  expanded,
  onToggle,
  readOnly,
}: {
  machineId: string
  instanceId: string
  session: SessionSummary
  depth: number
  hasChildren: boolean
  expanded: boolean
  onToggle: () => void
  readOnly: boolean
}) {
  const activity =
    session.displayState === 'Running' ||
    session.displayState === 'Retrying' ||
    session.displayState === 'WaitingPermission' ||
    session.displayState === 'Stalled'
      ? formatElapsed(session.busySince)
      : formatRelative(session.lastActivityAt ?? session.updatedAt)

  const href = `/m/${encodeURIComponent(machineId)}/agents/${encodeURIComponent(instanceId)}/sessions/${encodeURIComponent(session.ref.sessionId)}`

  return (
    <div
      className={cx(styles.sessionRow, depth > 0 && styles.sessionChild)}
      style={{ marginInlineStart: `${Math.min(depth, 4) * 14}px` }}
    >
      <div className={styles.treeControl}>
        {hasChildren ? (
          <button
            type="button"
            className={cx(styles.expandButton, expanded && styles.expandOpen)}
            aria-label={expanded ? '자식 세션 접기' : '자식 세션 펼치기'}
            aria-expanded={expanded}
            onClick={onToggle}
          >
            <ChevronRight size={15} aria-hidden="true" />
          </button>
        ) : depth > 0 ? (
          <FolderTree size={13} className={styles.childIcon} aria-hidden="true" />
        ) : null}
      </div>

      <Link to={href} className={styles.sessionMain}>
        <div className={styles.sessionTitleRow}>
          <p className={styles.sessionTitle}>{session.title || '(제목 없음)'}</p>
          {session.activeOnInstance ? null : <Badge tone="neutral">비활성 인스턴스</Badge>}
          {readOnly ? <Badge tone="neutral">읽기 전용</Badge> : null}
        </div>
        <p className={styles.sessionDirectory}>{session.directory || '—'}</p>
        <div className={styles.sessionMeta}>
          <span>{activity}</span>
          <span aria-hidden="true">·</span>
          <span>{session.lastModel?.modelId ?? '모델 미확인'}</span>
          {session.messageCount !== null ? (
            <>
              <span aria-hidden="true">·</span>
              <span>메시지 {session.messageCount}</span>
            </>
          ) : null}
        </div>
        {session.retry ? (
          <p className={styles.retryLine}>
            재시도 {session.retry.attempt}회
            {session.retry.message ? ` · ${session.retry.message}` : ''}
          </p>
        ) : null}
        {session.lastErrorMessage &&
        (session.displayState === 'Errored' ||
          session.displayState === 'QuotaBlocked' ||
          session.displayState === 'Stalled') ? (
          <p className={styles.errorLine}>{session.lastErrorMessage}</p>
        ) : null}
      </Link>

      <div className={styles.sessionStatus}>
        <StatusPill state={session.displayState} />
        <ChevronRight size={16} className={styles.rowChevron} aria-hidden="true" />
      </div>
    </div>
  )
}

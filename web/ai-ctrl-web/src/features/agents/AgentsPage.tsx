import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { ChevronDown, ChevronRight, FolderTree, Plus, RefreshCw, Server } from 'lucide-react'
import {
  useCreateSession,
  useInstances,
  useMachine,
  useModels,
  useRefreshInstances,
  useSessions,
  useWorkspaces,
} from '../../api/queries'
import { fetchMachineFs } from '../../api/client'
import { describeApiError } from '../../api/errors'
import { modeLabels, providerLabels } from '../../api/labels'
import type {
  InstanceInfo,
  SessionCreateResult,
  SessionSummary,
} from '../../api/types'
import { formatElapsed, formatRelative } from '../../api/format'
import { buildSessionRows, sortSessions } from './sessionOrder'
import { Badge } from '../../components/Badge'
import { Button } from '../../components/Button'
import { DirectoryPickerDialog } from '../../components/DirectoryPickerDialog'
import { FreshnessBadge } from '../../components/FreshnessBadge'
import { InlineNotice } from '../../components/InlineNotice'
import { PageHeader } from '../../components/PageHeader'
import { StatusPill } from '../../components/StatusPill'
import { EmptyBlock, ErrorBlock, LoadingBlock } from '../../components/AsyncState'
import { useToast } from '../../components/Toast'
import { instanceLabel } from '../../app/featureRoutes'
import { keyToModel, modelToKey } from '../session/modelValue'
import { VpnFailoverQuickPanel } from '../vpn/VpnFailoverQuickPanel'
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

      <InlineNotice tone="info" title="새 세션 만들기">
        인스턴스를 선택하고 <strong>새 세션</strong>을 누르면 워크스페이스에서 세션을 만들고 첫
        프롬프트를 보낼 수 있습니다. SharedStore 인스턴스는 읽기 전용입니다.
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
  const [creating, setCreating] = useState(false)

  const rows = buildSessionRows(sessions.data?.data.sessions ?? [], expanded)
  const managed = instance.managed
  const canCreate = managed && instance.healthy && machine.data?.data.online !== false

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
              tone="primary"
              size="sm"
              icon={<Plus size={15} aria-hidden="true" />}
              disabled={!canCreate}
              onClick={() => setCreating((value) => !value)}
            >
              새 세션
            </Button>
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

      {creating ? (
        <CreateSessionPanel
          machineId={machineId}
          instanceId={instance.id.value}
          onClose={() => setCreating(false)}
        />
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

function CreateSessionPanel({
  machineId,
  instanceId,
  onClose,
}: {
  machineId: string
  instanceId: string
  onClose: () => void
}) {
  const toast = useToast()
  const navigate = useNavigate()
  const workspaces = useWorkspaces(machineId)
  const models = useModels(machineId, instanceId)
  const create = useCreateSession(machineId, instanceId)
  const [directory, setDirectory] = useState<string | null>(null)
  const [pickerOpen, setPickerOpen] = useState(false)
  const [checkingPath, setCheckingPath] = useState(false)
  const [title, setTitle] = useState('')
  const [initialPrompt, setInitialPrompt] = useState('')
  const [modelKey, setModelKey] = useState('')

  const workspaceList = workspaces.data?.data ?? []
  const defaultDirectory = workspaceList[0]?.root ?? ''
  const directoryValue = directory ?? defaultDirectory
  const modelList = models.data?.data ?? []

  const submit = async () => {
    const target = directoryValue.trim()
    if (!target) {
      toast.notify('error', '워킹 디렉토리를 입력하거나 경로 선택 버튼으로 선택하세요.')
      return
    }

    if (create.isPending || checkingPath) {
      return
    }

    setCheckingPath(true)
    try {
      await fetchMachineFs(machineId, target)
    } catch (caught) {
      toast.notify('error', `경로를 확인할 수 없습니다: ${describeApiError(caught).message}`)
      return
    } finally {
      setCheckingPath(false)
    }

    create.mutate(
      {
        directory: target,
        title: title.trim() || null,
        initialPrompt: initialPrompt.trim() || null,
        model: keyToModel(modelKey),
      },
      {
        onSuccess: (result) => {
          if (result.data.kind === 'pending') {
            toast.notify('info', '세션 생성 명령을 접수했습니다. 목록을 새로고침하세요.')
            onClose()
            return
          }

          const command = result.data.command
          if (command.status !== 'Succeeded') {
            toast.notify(
              'error',
              command.errorMessage ?? '세션을 만들지 못했습니다.',
            )
            return
          }

          const created = asSessionCreateResult(command.result)
          if (!created) {
            toast.notify('error', '세션 생성 결과를 해석하지 못했습니다.')
            onClose()
            return
          }

          if (created.promptError) {
            toast.notify(
              'error',
              `세션은 만들었지만 첫 프롬프트는 실패했습니다: ${created.promptError}`,
            )
          } else {
            toast.notify('success', '세션을 만들었습니다.')
          }

          onClose()
          navigate(
            `/m/${encodeURIComponent(machineId)}/agents/${encodeURIComponent(instanceId)}/sessions/${encodeURIComponent(created.session.ref.sessionId)}`,
          )
        },
        onError: (error) => {
          toast.notify('error', describeApiError(error).message)
        },
      },
    )
  }

  return (
    <section className={styles.createPanel} aria-label="새 세션 만들기">
      <div className={styles.createHeader}>
        <h2 className={styles.createTitle}>새 세션</h2>
        <button type="button" className={styles.createClose} onClick={onClose}>
          닫기
        </button>
      </div>

      <form
        className={styles.createForm}
        onSubmit={(event) => {
          event.preventDefault()
          void submit()
        }}
      >
        <div className={styles.createFields}>
          <div className={styles.createFieldWide}>
            <span>워킹 디렉토리</span>
            <div className={styles.pathRow}>
              <input
                value={directoryValue}
                onChange={(event) => setDirectory(event.target.value)}
                placeholder="C:\Users\... (해당 머신의 절대 경로)"
                spellCheck={false}
                disabled={create.isPending}
                aria-label="워킹 디렉토리"
              />
              <Button
                size="sm"
                tone="secondary"
                icon={<FolderTree size={14} aria-hidden="true" />}
                onClick={() => setPickerOpen(true)}
                disabled={create.isPending}
              >
                경로 선택
              </Button>
            </div>
            <span className={styles.createHint}>
              직접 입력하거나 경로 선택에서 드라이브부터 트리로 찾을 수 있습니다.
            </span>
          </div>

          <label className={styles.createField}>
            <span>모델</span>
            <div className={styles.selectWrap}>
              <select
                value={modelKey}
                onChange={(event) => setModelKey(event.target.value)}
                disabled={models.isLoading}
              >
                <option value="">세션 기본 모델</option>
                {modelList.map((model) => (
                  <option key={modelToKey(model)} value={modelToKey(model)}>
                    {model.name ?? model.modelId}
                  </option>
                ))}
              </select>
              <ChevronDown size={14} className={styles.selectIcon} aria-hidden="true" />
            </div>
          </label>

          <label className={styles.createFieldWide}>
            <span>제목 (선택)</span>
            <input
              value={title}
              onChange={(event) => setTitle(event.target.value)}
              placeholder="예: 로그인 버그 수정"
              maxLength={200}
            />
          </label>

          <label className={styles.createFieldWide}>
            <span>첫 프롬프트 (선택)</span>
            <textarea
              value={initialPrompt}
              onChange={(event) => setInitialPrompt(event.target.value)}
              placeholder="세션을 만든 뒤 바로 보낼 프롬프트를 입력하세요."
              rows={3}
            />
          </label>

          <div className={styles.createSection}>
            <VpnFailoverQuickPanel machineId={machineId} />
          </div>
        </div>

        {workspaces.isError ? (
          <p className={styles.createError}>
            기본 워크스페이스 경로를 불러오지 못했습니다. 경로를 직접 입력하세요.
          </p>
        ) : null}

        <div className={styles.createActions}>
          <p className={styles.createHint}>
            세션은 입력한 디렉토리에서 생성됩니다. 존재하지 않는 경로는 만들 수
            없습니다.
          </p>
          <Button
            type="submit"
            tone="primary"
            icon={<Plus size={15} aria-hidden="true" />}
            loading={create.isPending || checkingPath}
          >
            세션 만들기
          </Button>
        </div>
      </form>

      {pickerOpen ? (
        <DirectoryPickerDialog
          machineId={machineId}
          initialPath={directoryValue}
          onSelect={setDirectory}
          onClose={() => setPickerOpen(false)}
        />
      ) : null}
    </section>
  )
}

function asSessionCreateResult(value: unknown): SessionCreateResult | null {
  if (typeof value !== 'object' || value === null) {
    return null
  }

  const candidate = value as Partial<SessionCreateResult>
  if (!candidate.session || typeof candidate.session.ref?.sessionId !== 'string') {
    return null
  }

  return candidate as SessionCreateResult
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

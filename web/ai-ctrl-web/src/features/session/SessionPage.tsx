import { useEffect, useMemo, useRef, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useVirtualizer } from '@tanstack/react-virtual'
import { useQueryClient } from '@tanstack/react-query'
import {
  ArrowLeft,
  ChevronDown,
  CircleStop,
  LoaderCircle,
  MessageSquare,
} from 'lucide-react'
import { ulid } from 'ulid'
import type { ModelRef, SessionSummary } from '../../api/types'
import {
  useAbortCommand,
  useInstances,
  useMachine,
  useMessages,
  useModels,
  usePromptCommand,
  useSession,
  queryKeys,
} from '../../api/queries'
import { describeApiError } from '../../api/errors'
import { formatRelative, shortId } from '../../api/format'
import { displayStateLabels } from '../../api/labels'
import { Button } from '../../components/Button'
import { FreshnessBadge } from '../../components/FreshnessBadge'
import { InlineNotice } from '../../components/InlineNotice'
import { PageHeader } from '../../components/PageHeader'
import { StatusPill } from '../../components/StatusPill'
import { EmptyBlock, ErrorBlock, LoadingBlock } from '../../components/AsyncState'
import { useToast } from '../../components/Toast'
import { instanceLabel } from '../../app/featureRoutes'
import {
  pendingForSession,
  sessionKeyOf,
  usePendingPromptStore,
  type PendingPrompt,
} from '../../flux/pendingPromptStore'
import { getSessionActions } from './sessionActions'
import { MessageBubble } from './MessageBubble'
import { PromptComposer } from './PromptComposer'
import { PendingPromptBubble } from './PendingPromptBubble'
import { keyToModel, modelToKey } from './modelValue'
import styles from './SessionPage.module.css'

export function SessionPage() {
  const { machineId, instanceId, sessionId } = useParams<{
    machineId: string
    instanceId: string
    sessionId: string
  }>()
  const toast = useToast()
  const queryClient = useQueryClient()
  const sessionQuery = useSession(machineId, instanceId, sessionId)
  const instanceQuery = useInstances(machineId)
  const machineQuery = useMachine(machineId)
  const models = useModels(machineId, instanceId)
  const messages = useMessages(machineId, instanceId, sessionId)
  const promptMutation = usePromptCommand(
    machineId ?? '',
    instanceId ?? '',
    sessionId ?? '',
  )
  const abortMutation = useAbortCommand(
    machineId ?? '',
    instanceId ?? '',
    sessionId ?? '',
  )
  const pendingItems = usePendingPromptStore((state) => state.items)
  const addPending = usePendingPromptStore((state) => state.add)
  const setPendingStatus = usePendingPromptStore((state) => state.setStatus)
  const setPendingCommandId = usePendingPromptStore((state) => state.setCommandId)
  const removePending = usePendingPromptStore((state) => state.remove)

  const [text, setText] = useState('')
  const [modelKey, setModelKey] = useState('')
  const modelInitialized = useRef(false)
  const scrollRef = useRef<HTMLDivElement>(null)
  const previousCount = useRef(0)
  const initializedScroll = useRef(false)

  const session = sessionQuery.data?.data
  const instance = instanceQuery.data?.data.find(
    (item) => item.id.value === instanceId,
  )
  const managed = instance?.managed ?? false
  const machineOnline = machineQuery.data?.data.online ?? true
  const actions = getSessionActions(session?.displayState, managed)
  const canPrompt = managed && machineOnline && (actions.send || actions.abortAndSend)
  const promptMode = actions.send ? 'prompt' : 'abort-and-prompt'
  const pending = machineId && instanceId && sessionId
    ? pendingForSession(pendingItems, sessionKeyOf(machineId, instanceId, sessionId))
    : []

  useEffect(() => {
    if (modelInitialized.current) {
      return
    }
    if (session?.lastModel) {
      setModelKey(modelToKey(session.lastModel))
      modelInitialized.current = true
      return
    }
    const first = models.data?.data[0]
    if (first) {
      setModelKey(modelToKey(first))
      modelInitialized.current = true
    }
  }, [models.data, session?.lastModel])

  useEffect(() => {
    if (session?.title) {
      document.title = `${session.title} · AI Ctrl`
    }
    return () => {
      document.title = 'AI Ctrl Agent'
    }
  }, [session?.title])

  const allMessages = useMemo(() => {
    const pages = messages.data?.pages ?? []
    const seen = new Set<string>()
    const result = []
    for (const page of pages) {
      for (const message of page.data.messages) {
        if (!seen.has(message.messageId)) {
          seen.add(message.messageId)
          result.push(message)
        }
      }
    }
    return result
  }, [messages.data])

  const virtualizer = useVirtualizer({
    count: allMessages.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => 150,
    overscan: 7,
  })

  useEffect(() => {
    const element = scrollRef.current
    if (!element || allMessages.length === 0) {
      return
    }
    const nearBottom =
      element.scrollHeight - element.scrollTop - element.clientHeight < 180
    if (!initializedScroll.current || (allMessages.length > previousCount.current && nearBottom)) {
      const frame = window.requestAnimationFrame(() => {
        element.scrollTop = element.scrollHeight
      })
      initializedScroll.current = true
      previousCount.current = allMessages.length
      return () => window.cancelAnimationFrame(frame)
    }
    previousCount.current = allMessages.length
    return undefined
  }, [allMessages.length])

  if (!machineId || !instanceId || !sessionId) {
    return <EmptyBlock title="세션 주소가 올바르지 않습니다" />
  }

  if (sessionQuery.isLoading) {
    return <LoadingBlock label="세션을 불러오는 중…" />
  }

  if (sessionQuery.isError) {
    return (
      <div className={styles.page}>
        <ErrorBlock
          error={sessionQuery.error}
          onRetry={() => void sessionQuery.refetch()}
        />
        <Link
          to={`/m/${encodeURIComponent(machineId)}/agents/${encodeURIComponent(instanceId)}`}
          className={styles.backLink}
        >
          에이전트 세션 목록으로 돌아가기
        </Link>
      </div>
    )
  }

  if (!session) {
    return <EmptyBlock title="세션을 찾을 수 없습니다" />
  }

  const selectedModel = keyToModel(modelKey)
  const submitPrompt = (
    requestId: string,
    promptText: string,
    mode: 'prompt' | 'abort-and-prompt',
    model: ModelRef | null,
  ) => {
    promptMutation.mutate(
      {
        input: { text: promptText, model },
        requestId,
        mode,
      },
      {
        onSuccess: (result) => {
          setPendingCommandId(requestId, result.data.id)
          setPendingStatus(requestId, 'sending')
          void queryClient.invalidateQueries({
            queryKey: queryKeys.command(result.data.id),
          })
        },
        onError: (error) => {
          const message = describeApiError(error).message
          setPendingStatus(requestId, 'failed', message)
          toast.notify('error', message)
        },
      },
    )
  }

  const handleSend = () => {
    const trimmed = text.trim()
    if (!trimmed || !canPrompt) {
      return
    }
    const requestId = ulid()
    addPending({
      id: requestId,
      sessionKey: sessionKeyOf(machineId, instanceId, sessionId),
      text: trimmed,
      model: selectedModel,
      mode: promptMode,
      status: 'sending',
      error: null,
      commandId: null,
      createdAt: Date.now(),
    })
    setText('')
    submitPrompt(requestId, trimmed, promptMode, selectedModel)
  }

  const handleRetry = (prompt: PendingPrompt) => {
    if (prompt.commandId) {
      return
    }
    setPendingStatus(prompt.id, 'sending', null)
    submitPrompt(prompt.id, prompt.text, prompt.mode, prompt.model)
  }

  const handleAbort = () => {
    abortMutation.mutate(undefined, {
      onSuccess: () => {
        toast.notify('success', '중단 명령을 전송했습니다.')
      },
      onError: (error) => {
        toast.notify('error', describeApiError(error).message)
      },
    })
  }

  const notice = buildSessionNotice({
    machineOnline,
    instanceLoaded: Boolean(instance),
    managed,
    session,
    lastErrorMessage: session.lastErrorMessage,
  })

  const disabledReason = !managed
    ? '관리형 인스턴스가 아니어서 읽기 전용입니다.'
    : !machineOnline
      ? '머신이 오프라인입니다.'
      : session.displayState === 'ForeignBusy'
        ? '다른 앱이 세션을 처리 중입니다.'
        : session.displayState === 'Unknown'
          ? '세션 상태를 확인할 수 없습니다.'
          : actions.abort && !actions.abortAndSend
            ? '실행 중인 세션은 중단 후 전송할 수 있습니다.'
            : null

  return (
    <div className={styles.page}>
      <PageHeader
        eyebrow={
          instance ? instanceLabel(instance) : 'AI Agent session'
        }
        title={session.title || '(제목 없는 세션)'}
        description={`${session.directory || '작업 디렉터리 미확인'} · ${shortId(session.ref.sessionId)}`}
        meta={
          <>
            <StatusPill state={session.displayState} />
            {sessionQuery.data ? <FreshnessBadge meta={sessionQuery.data.meta} /> : null}
          </>
        }
        actions={
          <>
            <Link
              to={`/m/${encodeURIComponent(machineId)}/agents/${encodeURIComponent(instanceId)}`}
              className={styles.backLink}
            >
              <ArrowLeft size={14} aria-hidden="true" />
              세션 목록
            </Link>
            {actions.abort ? (
              <Button
                tone="danger"
                size="sm"
                loading={abortMutation.isPending}
                disabled={!machineOnline}
                icon={<CircleStop size={14} aria-hidden="true" />}
                onClick={handleAbort}
              >
                중단
              </Button>
            ) : null}
          </>
        }
      />

      {notice}
      {session.retry ? (
        <InlineNotice tone="warn" title={`재시도 ${session.retry.attempt}회`}>
          {session.retry.message ?? '모델 호출을 다시 시도하는 중입니다.'}
          {session.retry.nextAt ? ` · 다음 시도 ${formatRelative(session.retry.nextAt)}` : ''}
        </InlineNotice>
      ) : null}

      <section className={styles.chat} aria-labelledby="conversation-title">
        <div className={styles.chatHeader}>
          <div className={styles.chatTitleGroup}>
            <MessageSquare size={15} aria-hidden="true" />
            <h2 id="conversation-title" className={styles.chatTitle}>
              대화
            </h2>
            <span className={styles.count}>{allMessages.length}</span>
            <span className={styles.stateText}>
              {displayStateLabels[session.displayState] ?? session.displayState}
            </span>
          </div>
          <div className={styles.chatMeta}>
            <span>{formatRelative(session.lastActivityAt ?? session.updatedAt)}</span>
            {messages.data?.pages[0] ? (
              <FreshnessBadge meta={messages.data.pages[0].meta} />
            ) : null}
            <button
              type="button"
              className={styles.jumpButton}
              aria-label="대화 맨 아래로 이동"
              onClick={() => {
                const element = scrollRef.current
                if (element) {
                  element.scrollTop = element.scrollHeight
                }
              }}
            >
              <ChevronDown size={15} aria-hidden="true" />
            </button>
          </div>
        </div>

        {messages.isLoading ? (
          <LoadingBlock label="메시지를 불러오는 중…" />
        ) : messages.isError ? (
          <div className={styles.chatError}>
            <ErrorBlock
              error={messages.error}
              onRetry={() => void messages.refetch()}
              compact
            />
          </div>
        ) : allMessages.length === 0 ? (
          <div className={styles.emptyChat}>
            <EmptyBlock
              title="아직 메시지가 없습니다"
              description="첫 프롬프트를 보내 대화를 시작하세요."
            />
          </div>
        ) : (
          <div className={styles.scroll} ref={scrollRef} role="log" aria-label="세션 메시지">
            <div
              className={styles.virtualInner}
              style={{ height: `${virtualizer.getTotalSize()}px` }}
            >
              {virtualizer.getVirtualItems().map((virtualRow) => {
                const message = allMessages[virtualRow.index]
                return (
                  <div
                    key={message.messageId}
                    data-index={virtualRow.index}
                    ref={virtualizer.measureElement}
                    className={styles.virtualRow}
                    style={{ transform: `translateY(${virtualRow.start}px)` }}
                  >
                    <MessageBubble message={message} />
                  </div>
                )
              })}
            </div>
          </div>
        )}

        {messages.hasNextPage ? (
          <div className={styles.pagination}>
            <Button
              tone="ghost"
              size="sm"
              loading={messages.isFetchingNextPage}
              onClick={() => void messages.fetchNextPage()}
            >
              메시지 더 불러오기
            </Button>
          </div>
        ) : null}

        {pending.length > 0 ? (
          <div className={styles.pendingList}>
            {pending.map((prompt) => (
              <PendingPromptBubble
                key={prompt.id}
                prompt={prompt}
                machineId={machineId}
                instanceId={instanceId}
                sessionId={sessionId}
                onRetry={handleRetry}
                onRemove={removePending}
              />
            ))}
          </div>
        ) : null}

        <PromptComposer
          text={text}
          onTextChange={setText}
          models={models.data?.data ?? []}
          modelsLoading={models.isLoading}
          selectedModelKey={modelKey}
          onModelChange={(key) => {
            modelInitialized.current = true
            setModelKey(key)
          }}
          disabled={!canPrompt}
          disabledReason={disabledReason}
          mode={promptMode}
          sending={promptMutation.isPending}
          onSend={handleSend}
        />
      </section>

      {messages.isFetching && !messages.isLoading ? (
        <p className={styles.polling}>
          <LoaderCircle className={styles.spin} size={12} aria-hidden="true" />
          실시간 업데이트 중
        </p>
      ) : null}
    </div>
  )
}

function buildSessionNotice({
  machineOnline,
  instanceLoaded,
  managed,
  session,
  lastErrorMessage,
}: {
  machineOnline: boolean
  instanceLoaded: boolean
  managed: boolean
  session: SessionSummary
  lastErrorMessage: string | null
}) {
  if (!machineOnline) {
    return (
      <InlineNotice tone="warn" title="머신 오프라인">
        캐시된 대화만 표시됩니다. 머신이 다시 연결될 때까지 프롬프트 전송과 중단을 사용할 수
        없습니다.
      </InlineNotice>
    )
  }
  if (instanceLoaded && !managed) {
    return (
      <InlineNotice tone="warn" title="읽기 전용 인스턴스">
        SharedStore 모드에서는 다른 앱과 충돌을 피하기 위해 웹에서 프롬프트를 보낼 수 없습니다.
      </InlineNotice>
    )
  }
  if (session.displayState === 'WaitingPermission') {
    return (
      <InlineNotice tone="warn" title="권한 승인 대기 중">
        M4 API에는 권한 승인 카드가 없습니다. 웹에서는 중단하거나 중단 후 새 프롬프트를 보낼 수
        있습니다. 승인이 필요하면 Desktop에서 처리하세요.
      </InlineNotice>
    )
  }
  if (session.displayState === 'ForeignBusy') {
    return (
      <InlineNotice tone="warn" title="다른 앱이 세션을 처리 중입니다">
        Desktop 또는 다른 클라이언트가 이 세션을 소유하고 있습니다. 읽기 전용으로 표시됩니다.
      </InlineNotice>
    )
  }
  if (session.displayState === 'Unknown') {
    return (
      <InlineNotice tone="warn" title="세션 상태를 확인할 수 없습니다">
        인스턴스 상태가 불확실하여 프롬프트 전송을 비활성화했습니다.
      </InlineNotice>
    )
  }
  if (session.displayState === 'QuotaBlocked') {
    return (
      <InlineNotice tone="danger" title="할당량 또는 속도 제한">
        {lastErrorMessage ?? '모델 할당량이 회복된 뒤 다시 시도하세요.'}
      </InlineNotice>
    )
  }
  if (session.displayState === 'Errored' && lastErrorMessage) {
    return (
      <InlineNotice tone="danger" title="마지막 턴에서 오류가 발생했습니다">
        {lastErrorMessage}
      </InlineNotice>
    )
  }
  if (session.displayState === 'Stalled') {
    return (
      <InlineNotice tone="warn" title="출력이 멈춘 것으로 보입니다">
        마지막 활동 후 10분 이상 새 메시지가 없습니다. 필요하면 중단 후 다시 전송하세요.
      </InlineNotice>
    )
  }
  return null
}

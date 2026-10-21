import { useEffect, useRef } from 'react'
import { Link } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, LoaderCircle, RotateCcw, Trash2 } from 'lucide-react'
import type { PendingPrompt } from '../../flux/pendingPromptStore'
import { usePendingPromptStore } from '../../flux/pendingPromptStore'
import { useCommandFollow, isTerminalCommandStatus, queryKeys } from '../../api/queries'
import { commandStatusLabels } from '../../api/labels'
import { Button } from '../../components/Button'
import { useToast } from '../../components/Toast'
import styles from './PendingPromptBubble.module.css'

export function PendingPromptBubble({
  prompt,
  machineId,
  instanceId,
  sessionId,
  onRetry,
  onRemove,
}: {
  prompt: PendingPrompt
  machineId: string
  instanceId: string
  sessionId: string
  onRetry: (prompt: PendingPrompt) => void
  onRemove: (id: string) => void
}) {
  const queryClient = useQueryClient()
  const toast = useToast()
  const setStatus = usePendingPromptStore((state) => state.setStatus)
  const follow = useCommandFollow(prompt.commandId)
  const handled = useRef<string | null>(null)

  useEffect(() => {
    if (
      !prompt.commandId ||
      !follow.status ||
      !isTerminalCommandStatus(follow.status) ||
      handled.current === prompt.commandId
    ) {
      return
    }
    handled.current = prompt.commandId

    if (follow.status === 'Succeeded') {
      toast.notify('success', '프롬프트가 전달되었습니다.')
      void queryClient.invalidateQueries({
        queryKey: queryKeys.messages(machineId, instanceId, sessionId),
      })
      void queryClient.invalidateQueries({
        queryKey: queryKeys.session(machineId, instanceId, sessionId),
      })
      const timer = window.setTimeout(() => onRemove(prompt.id), 900)
      return () => window.clearTimeout(timer)
    }

    const message =
      follow.command?.errorMessage ??
      `명령이 ${commandStatusLabels[follow.status] ?? follow.status} 상태로 종료되었습니다.`
    setStatus(prompt.id, 'failed', message)
    toast.notify('error', message)
    return undefined
  }, [
    follow.command?.errorMessage,
    follow.status,
    machineId,
    instanceId,
    onRemove,
    prompt.commandId,
    prompt.id,
    queryClient,
    sessionId,
    setStatus,
    toast,
  ])

  useEffect(() => {
    if (follow.expired && prompt.status === 'sending') {
      setStatus(
        prompt.id,
        'failed',
        '60초 안에 명령 결과를 확인하지 못했습니다. 명령 이력에서 최종 상태를 확인하세요.',
      )
      toast.notify('error', '명령 결과 확인 시간이 초과되었습니다.')
    }
  }, [follow.expired, prompt.id, prompt.status, setStatus, toast])

  const waiting = prompt.status === 'sending'

  return (
    <article
      className={waiting ? styles.pending : styles.failed}
      aria-live="polite"
    >
      <div className={styles.header}>
        <span className={styles.label}>내 프롬프트</span>
        {waiting ? (
          <span className={styles.status}>
            <LoaderCircle className={styles.spinner} size={13} aria-hidden="true" />
            {follow.status
              ? `${commandStatusLabels[follow.status] ?? follow.status} · 결과 확인 중`
              : '전송 중…'}
          </span>
        ) : (
          <span className={styles.failedStatus}>
            <AlertTriangle size={13} aria-hidden="true" />
            전송 실패
          </span>
        )}
      </div>

      <p className={styles.text}>{prompt.text}</p>

      {follow.command?.steps.length ? (
        <p className={styles.step}>
          마지막 단계: {follow.command.steps[follow.command.steps.length - 1].step}
        </p>
      ) : null}

      {prompt.error ? <p className={styles.error}>{prompt.error}</p> : null}
      {follow.error && waiting ? (
        <p className={styles.hint}>명령 상태 확인이 지연되고 있습니다.</p>
      ) : null}

      {!waiting ? (
        <div className={styles.actions}>
          {!prompt.commandId ? (
            <Button
              tone="secondary"
              size="sm"
              icon={<RotateCcw size={13} aria-hidden="true" />}
              onClick={() => onRetry(prompt)}
            >
              다시 시도
            </Button>
          ) : null}
          {prompt.commandId ? (
            <Link to={`/commands/${encodeURIComponent(prompt.commandId)}`} className={styles.link}>
              명령 이력 보기
            </Link>
          ) : null}
          <button
            type="button"
            className={styles.remove}
            onClick={() => onRemove(prompt.id)}
            aria-label="실패한 프롬프트 제거"
          >
            <Trash2 size={14} aria-hidden="true" />
            삭제
          </button>
        </div>
      ) : null}
    </article>
  )
}

import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { RefreshCw } from 'lucide-react'
import { useCommands, useMachines, isTerminalCommandStatus } from '../../api/queries'
import type { CommandFilters, CommandStatus } from '../../api/types'
import { commandStatusLabels } from '../../api/labels'
import { formatRelative } from '../../api/format'
import { Badge, type BadgeTone } from '../../components/Badge'
import { Button } from '../../components/Button'
import { EmptyBlock, ErrorBlock, LoadingBlock } from '../../components/AsyncState'
import { FreshnessBadge } from '../../components/FreshnessBadge'
import { PageHeader } from '../../components/PageHeader'
import styles from './CommandsPage.module.css'

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

const statusOptions: CommandStatus[] = [
  'Created',
  'Queued',
  'Dispatched',
  'Acknowledged',
  'Running',
  'Succeeded',
  'Failed',
  'Cancelled',
  'Expired',
  'Unknown',
]

export function CommandsPage() {
  const [machineId, setMachineId] = useState('')
  const [status, setStatus] = useState<CommandStatus | ''>('')
  const machines = useMachines()
  const filters: CommandFilters = useMemo(
    () => ({
      machineId: machineId || undefined,
      status: status || undefined,
      limit: 100,
    }),
    [machineId, status],
  )
  const commands = useCommands(filters)
  const list = commands.data?.data ?? []
  const pendingCount = list.filter(
    (command) => !isTerminalCommandStatus(command.status),
  ).length

  return (
    <div className={styles.page}>
      <PageHeader
        eyebrow="Commands"
        title="명령 이력"
        description="프롬프트, 중단, 새로고침 등 서버가 발행한 명령의 상태를 추적합니다."
        meta={commands.data ? <FreshnessBadge meta={commands.data.meta} /> : null}
        actions={
          <Button
            tone="secondary"
            size="sm"
            icon={<RefreshCw size={14} aria-hidden="true" />}
            loading={commands.isFetching && !commands.isLoading}
            onClick={() => void commands.refetch()}
          >
            새로고침
          </Button>
        }
      />

      <div className={styles.filters}>
        <select
          className={styles.select}
          value={machineId}
          onChange={(event) => setMachineId(event.target.value)}
          aria-label="머신 필터"
        >
          <option value="">모든 머신</option>
          {(machines.data?.data ?? []).map((machine) => (
            <option key={machine.id.value} value={machine.id.value}>
              {machine.hostname}
            </option>
          ))}
        </select>
        <select
          className={styles.select}
          value={status}
          onChange={(event) => setStatus(event.target.value as CommandStatus | '')}
          aria-label="상태 필터"
        >
          <option value="">모든 상태</option>
          {statusOptions.map((option) => (
            <option key={option} value={option}>
              {commandStatusLabels[option]}
            </option>
          ))}
        </select>
        {pendingCount > 0 ? (
          <Badge tone="info">진행 중 {pendingCount} · 1초 자동 갱신</Badge>
        ) : (
          <span className={styles.filterHint}>진행 중인 명령은 1초마다 갱신됩니다.</span>
        )}
      </div>

      {commands.isLoading ? (
        <LoadingBlock label="명령 이력을 불러오는 중…" />
      ) : commands.isError ? (
        <ErrorBlock error={commands.error} onRetry={() => void commands.refetch()} />
      ) : list.length === 0 ? (
        <EmptyBlock
          title="명령이 없습니다"
          description="필터를 바꾸거나 세션에서 프롬프트를 보내 명령을 생성하세요."
        />
      ) : (
        <ol className={styles.list}>
          {list.map((command) => {
            const lastStep = command.steps[command.steps.length - 1]
            return (
              <li key={command.commandId}>
                <Link
                  to={`/commands/${encodeURIComponent(command.commandId)}`}
                  className={styles.item}
                >
                  <div className={styles.itemHeader}>
                    <span className={styles.type}>{command.type}</span>
                    <Badge tone={statusTones[command.status]}>
                      {commandStatusLabels[command.status]}
                    </Badge>
                  </div>
                  <p className={styles.id}>{command.commandId}</p>
                  <div className={styles.meta}>
                    <span>{formatRelative(command.issuedAt)}</span>
                    <span aria-hidden="true">·</span>
                    <span>{command.issuedBy}</span>
                    {lastStep ? (
                      <>
                        <span aria-hidden="true">·</span>
                        <span>{lastStep.step}</span>
                      </>
                    ) : null}
                  </div>
                  {command.errorMessage ? (
                    <p className={styles.error}>{command.errorMessage}</p>
                  ) : null}
                </Link>
              </li>
            )
          })}
        </ol>
      )}

      <p className={styles.gapNote}>
        M4 명령 응답에는 machineId가 포함되지 않아 이 화면에서 머신별 표시를 보강할 수 없습니다.
      </p>
    </div>
  )
}

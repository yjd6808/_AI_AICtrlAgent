import { useState, type FormEvent } from 'react'
import { Save } from 'lucide-react'
import { describeApiError } from '../../api/errors'
import { formatRelative } from '../../api/format'
import { useUpdateVpnPolicy, useVpnPolicy } from '../../api/queries'
import type {
  VpnPolicyUpdateInput,
  VpnPolicyView,
  VpnServerPolicyStateView,
  VpnServerStatus,
} from '../../api/types'
import { Badge, type BadgeTone } from '../../components/Badge'
import { Button } from '../../components/Button'
import { InlineNotice } from '../../components/InlineNotice'
import { useToast } from '../../components/Toast'
import styles from './VpnPolicyPanel.module.css'

const statusLabels: Record<VpnServerStatus, string> = {
  ok: '정상',
  quota_exhausted: '할당량 소진',
  opencode_timeout: '응답 시간 초과',
  connect_failed: '연결 실패',
}

const statusTones: Record<VpnServerStatus, BadgeTone> = {
  ok: 'ok',
  quota_exhausted: 'danger',
  opencode_timeout: 'warn',
  connect_failed: 'danger',
}

type PolicyForm = {
  enabled: boolean
  minLoad: number
  maxLoad: number
  excludeUsedWithinHours: number
  maxAttemptsPerSession: number
  retryWaitThresholdSeconds: number
  countries: string
}

function toForm(policy: VpnPolicyView): PolicyForm {
  return {
    enabled: policy.enabled,
    minLoad: policy.minLoad,
    maxLoad: policy.maxLoad,
    excludeUsedWithinHours: policy.excludeUsedWithinHours,
    maxAttemptsPerSession: policy.maxAttemptsPerSession,
    retryWaitThresholdSeconds: policy.retryWaitThresholdSeconds,
    countries: policy.countries.join(', '),
  }
}

function toInput(form: PolicyForm): VpnPolicyUpdateInput {
  return {
    enabled: form.enabled,
    minLoad: form.minLoad,
    maxLoad: form.maxLoad,
    excludeUsedWithinHours: form.excludeUsedWithinHours,
    maxAttemptsPerSession: form.maxAttemptsPerSession,
    retryWaitThresholdSeconds: form.retryWaitThresholdSeconds,
    countries: form.countries
      .split(',')
      .map((value) => value.trim().toUpperCase())
      .filter(Boolean),
  }
}

function numberValue(value: string, fallback: number): number {
  const parsed = Number(value)
  return Number.isFinite(parsed) ? Math.trunc(parsed) : fallback
}

function ServerState({ state }: { state: VpnServerPolicyStateView }) {
  return (
    <tr>
      <td>
        <code>{state.server}</code>
      </td>
      <td>
        <Badge tone={statusTones[state.status]}>{statusLabels[state.status]}</Badge>
      </td>
      <td>{[state.country, state.city].filter(Boolean).join(' · ') || '-'}</td>
      <td>{state.lastUsedAt ? formatRelative(state.lastUsedAt) : '-'}</td>
      <td>{state.availableAt ? formatRelative(state.availableAt) : '-'}</td>
      <td>{state.failureCount}</td>
      <td className={styles.errorCell} title={state.lastError ?? undefined}>
        {state.lastError ?? '-'}
      </td>
    </tr>
  )
}

export function VpnPolicyPanel({ machineId }: { machineId: string }) {
  const toast = useToast()
  const policyQuery = useVpnPolicy(machineId)
  const update = useUpdateVpnPolicy(machineId)
  const policy = policyQuery.data?.data
  const [draft, setDraft] = useState<{ key: string; form: PolicyForm } | null>(null)
  const form =
    policy === undefined
      ? null
      : draft?.key === policy.updatedAt
        ? draft.form
        : toForm(policy)
  const setForm = (next: PolicyForm) =>
    setDraft({ key: policy?.updatedAt ?? '', form: next })

  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (!form) {
      return
    }
    update.mutate(toInput(form), {
      onSuccess: (result) => {
        if (result.data.kind === 'pending') {
          toast.notify('info', 'VPN 정책 저장 명령을 접수했습니다.')
          return
        }
        if (result.data.command.status === 'Failed') {
          toast.notify('error', result.data.command.errorMessage ?? 'VPN 정책을 저장하지 못했습니다.')
          return
        }
        toast.notify('success', 'VPN 정책을 저장했습니다.')
      },
      onError: (error) => toast.notify('error', describeApiError(error).message),
    })
  }

  if (policyQuery.isLoading) {
    return <InlineNotice tone="info" title="VPN 정책을 불러오는 중입니다">잠시만 기다려 주세요.</InlineNotice>
  }

  if (policyQuery.isError) {
    return (
      <InlineNotice tone="danger" title="VPN 정책을 불러오지 못했습니다">
        {describeApiError(policyQuery.error).message}
      </InlineNotice>
    )
  }

  if (!policy || !form) {
    return null
  }

  return (
    <section className={styles.panel}>
      <div className={styles.header}>
        <div>
          <p className={styles.eyebrow}>Failover policy</p>
          <h2 className={styles.title}>자동 VPN 전환</h2>
          <p className={styles.subtitle}>
            OpenCode 할당량 소진 또는 응답 시간 초과 시 현재 턴을 중단하고, 조건에 맞는 서버로
            전환한 뒤 같은 세션을 재개합니다.
          </p>
        </div>
        <Badge tone={policy.enabled ? 'ok' : 'neutral'}>
          {policy.enabled ? '사용' : '중지'}
        </Badge>
      </div>

      <form className={styles.form} onSubmit={submit}>
        <label className={styles.toggle}>
          <input
            type="checkbox"
            checked={form.enabled}
            onChange={(event) => setForm({ ...form, enabled: event.target.checked })}
          />
          <span>정책 사용</span>
        </label>

        <label className={styles.field}>
          <span>최소 부하</span>
          <input
            type="number"
            min={0}
            max={100}
            value={form.minLoad}
            onChange={(event) => setForm({ ...form, minLoad: numberValue(event.target.value, form.minLoad) })}
          />
        </label>

        <label className={styles.field}>
          <span>최대 부하</span>
          <input
            type="number"
            min={0}
            max={100}
            value={form.maxLoad}
            onChange={(event) => setForm({ ...form, maxLoad: numberValue(event.target.value, form.maxLoad) })}
          />
        </label>

        <label className={styles.field}>
          <span>재사용 제외 시간</span>
          <input
            type="number"
            min={0}
            value={form.excludeUsedWithinHours}
            onChange={(event) =>
              setForm({
                ...form,
                excludeUsedWithinHours: numberValue(event.target.value, form.excludeUsedWithinHours),
              })
            }
          />
        </label>

        <label className={styles.field}>
          <span>전환당 후보 시도</span>
          <input
            type="number"
            min={1}
            max={5}
            value={form.maxAttemptsPerSession}
            onChange={(event) =>
              setForm({
                ...form,
                maxAttemptsPerSession: numberValue(event.target.value, form.maxAttemptsPerSession),
              })
            }
          />
        </label>

        <label className={styles.field}>
          <span>같은 서버 대기 한계(초)</span>
          <input
            type="number"
            min={1}
            value={form.retryWaitThresholdSeconds}
            onChange={(event) =>
              setForm({
                ...form,
                retryWaitThresholdSeconds: numberValue(
                  event.target.value,
                  form.retryWaitThresholdSeconds,
                ),
              })
            }
          />
        </label>

        <label className={styles.fieldWide}>
          <span>허용 국가</span>
          <input
            type="text"
            placeholder="JP, NL, US"
            value={form.countries}
            onChange={(event) => setForm({ ...form, countries: event.target.value })}
          />
        </label>

        <div className={styles.actions}>
          <span className={styles.updated}>
            {policy.currentServer ? `현재 서버 ${policy.currentServer}` : '연결된 서버 없음'} ·{' '}
            {formatRelative(policy.updatedAt)} 갱신
          </span>
          <Button type="submit" size="sm" loading={update.isPending} icon={<Save size={14} />}>
            정책 저장
          </Button>
        </div>
      </form>

      <div className={styles.tableWrap}>
        <table className={styles.table}>
          <thead>
            <tr>
              <th>서버</th>
              <th>상태</th>
              <th>위치</th>
              <th>최근 사용</th>
              <th>사용 가능</th>
              <th>실패</th>
              <th>마지막 오류</th>
            </tr>
          </thead>
          <tbody>
            {policy.servers.length === 0 ? (
              <tr>
                <td colSpan={7} className={styles.empty}>
                  기록된 서버 상태가 없습니다.
                </td>
              </tr>
            ) : (
              policy.servers.map((state) => <ServerState key={state.server} state={state} />)
            )}
          </tbody>
        </table>
      </div>
    </section>
  )
}

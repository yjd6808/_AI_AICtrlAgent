import { useState, type FormEvent } from 'react'
import { BellRing, Trash2 } from 'lucide-react'
import { describeApiError } from '../../api/errors'
import { useMachines, useUpdateVpnRecovery, useVpnRecovery } from '../../api/queries'
import type { VpnRecoveryView } from '../../api/types'
import { Badge } from '../../components/Badge'
import { Button } from '../../components/Button'
import { InlineNotice } from '../../components/InlineNotice'
import { useToast } from '../../components/Toast'
import styles from './SettingsPage.module.css'

type Draft = {
  key: string
  enabled: boolean
  failureOnly: boolean
  webhook: string
}

function draftKey(machineId: string, recovery: VpnRecoveryView): string {
  return `${machineId}:${recovery.updatedAt}`
}

export function VpnRecoveryNotificationPanel() {
  const toast = useToast()
  const machinesQuery = useMachines()
  const machines = machinesQuery.data?.data ?? []
  const [selectedMachineId, setSelectedMachineId] = useState('')
  const machineId = selectedMachineId || machines[0]?.id.value || ''
  const recoveryQuery = useVpnRecovery(machineId || undefined)
  const update = useUpdateVpnRecovery(machineId)
  const recovery = recoveryQuery.data?.data
  const [draft, setDraft] = useState<Draft | null>(null)

  const key = recovery ? draftKey(machineId, recovery) : ''
  const form =
    draft && draft.key === key
      ? draft
      : {
          key,
          enabled: recovery?.discordEnabled ?? false,
          failureOnly: recovery?.discordNotifyOnFailureOnly ?? false,
          webhook: '',
        }
  const setForm = (next: Omit<Draft, 'key'>) => setDraft({ key, ...next })

  const save = (event: FormEvent) => {
    event.preventDefault()
    if (!machineId) {
      return
    }
    const webhook = form.webhook.trim()
    update.mutate(
      {
        discordEnabled: form.enabled,
        discordNotifyOnFailureOnly: form.failureOnly,
        ...(webhook ? { discordWebhookUrl: webhook } : {}),
      },
      {
        onSuccess: (result) => {
          if (result.data.kind === 'pending') {
            toast.notify('info', 'Discord 알림 설정 저장 명령을 접수했습니다.')
            setDraft(null)
            return
          }
          if (result.data.command.status === 'Failed') {
            toast.notify(
              'error',
              result.data.command.errorMessage ?? 'Discord 알림 설정을 저장하지 못했습니다.',
            )
            return
          }
          toast.notify('success', 'Discord 알림 설정을 저장했습니다.')
          setDraft(null)
        },
        onError: (error) => toast.notify('error', describeApiError(error).message),
      },
    )
  }

  const clearWebhook = () => {
    if (!machineId) {
      return
    }
    update.mutate(
      { discordWebhookUrl: '' },
      {
        onSuccess: (result) => {
          if (result.data.kind === 'pending') {
            toast.notify('info', 'Webhook 삭제 명령을 접수했습니다.')
            return
          }
          if (result.data.command.status === 'Failed') {
            toast.notify(
              'error',
              result.data.command.errorMessage ?? 'Webhook을 삭제하지 못했습니다.',
            )
            return
          }
          toast.notify('success', 'Discord webhook을 삭제했습니다.')
          setDraft(null)
        },
        onError: (error) => toast.notify('error', describeApiError(error).message),
      },
    )
  }

  return (
    <section className={styles.panel}>
      <div className={styles.panelHeader}>
        <BellRing size={16} aria-hidden="true" />
        <h2 className={styles.panelTitle}>Discord 장애 알림</h2>
        {recovery ? (
          <Badge tone={recovery.discordWebhookConfigured ? 'ok' : 'neutral'}>
            {recovery.discordWebhookConfigured ? 'Webhook 설정됨' : 'Webhook 없음'}
          </Badge>
        ) : null}
      </div>
      <p className={styles.hint}>
        복구 시도, VPN 전환, 재개 결과를 Discord webhook으로 전송합니다. Webhook URL은
        에이전트의 DPAPI 저장소에 보관되며 서버 캐시나 이 화면에는 다시 표시되지 않습니다.
      </p>

      {machinesQuery.isLoading ? (
        <p className={styles.hint}>머신 목록을 불러오는 중…</p>
      ) : machines.length === 0 ? (
        <InlineNotice tone="info" title="등록된 머신이 없습니다">
          머신을 등록한 뒤 Discord 알림을 설정할 수 있습니다.
        </InlineNotice>
      ) : (
        <>
          <label className={styles.field}>
            <span>머신</span>
            <select
              value={machineId}
              onChange={(event) => {
                setSelectedMachineId(event.target.value)
                setDraft(null)
              }}
            >
              {machines.map((machine) => (
                <option key={machine.id.value} value={machine.id.value}>
                  {machine.hostname} ({machine.id.value})
                </option>
              ))}
            </select>
          </label>

          {machinesQuery.isError ? (
            <InlineNotice tone="danger" title="머신 목록을 불러오지 못했습니다">
              {describeApiError(machinesQuery.error).message}
            </InlineNotice>
          ) : null}

          {recoveryQuery.isLoading ? (
            <p className={styles.hint}>복구 설정을 불러오는 중…</p>
          ) : recoveryQuery.isError ? (
            <InlineNotice tone="danger" title="복구 설정을 불러오지 못했습니다">
              {describeApiError(recoveryQuery.error).message}
            </InlineNotice>
          ) : recovery ? (
            <form className={styles.form} onSubmit={save}>
              <label className={styles.checkbox}>
                <input
                  type="checkbox"
                  checked={form.enabled}
                  onChange={(event) =>
                    setForm({ ...form, enabled: event.target.checked })
                  }
                />
                <span>Discord 알림 사용</span>
              </label>

              <label className={styles.checkbox}>
                <input
                  type="checkbox"
                  checked={form.failureOnly}
                  onChange={(event) =>
                    setForm({ ...form, failureOnly: event.target.checked })
                  }
                />
                <span>복구 실패·시도 초과만 알림</span>
              </label>

              <label className={styles.fieldWide}>
                <span>Webhook URL</span>
                <input
                  type="password"
                  autoComplete="off"
                  aria-label="Webhook URL"
                  value={form.webhook}
                  onChange={(event) => setForm({ ...form, webhook: event.target.value })}
                  placeholder={
                    recovery.discordWebhookConfigured
                      ? '저장됨 · 변경하려면 새 URL 입력'
                      : 'https://discord.com/api/webhooks/...'
                  }
                />
                <span className={styles.formHint}>
                  discord.com/api/webhooks/... 형식의 HTTPS URL만 저장됩니다.
                </span>
              </label>

              <div className={styles.formActions}>
                <span className={styles.hint}>
                  오류 복구가 켜져 있어야 알림이 전송됩니다.
                </span>
                <div className={styles.buttonRow}>
                  {recovery.discordWebhookConfigured ? (
                    <Button
                      tone="danger"
                      size="sm"
                      loading={update.isPending}
                      icon={<Trash2 size={13} aria-hidden="true" />}
                      onClick={clearWebhook}
                    >
                      Webhook 삭제
                    </Button>
                  ) : null}
                  <Button type="submit" size="sm" tone="primary" loading={update.isPending}>
                    알림 설정 저장
                  </Button>
                </div>
              </div>
            </form>
          ) : null}
        </>
      )}
    </section>
  )
}

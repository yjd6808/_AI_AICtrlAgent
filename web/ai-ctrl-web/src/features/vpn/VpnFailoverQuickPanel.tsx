import { useState } from 'react'
import { Link } from 'react-router-dom'
import { ArrowUpRight, Save } from 'lucide-react'
import { describeApiError } from '../../api/errors'
import { useUpdateVpnRecovery, useVpnRecovery } from '../../api/queries'
import type { VpnRecoveryUpdateInput } from '../../api/types'
import { Badge } from '../../components/Badge'
import { Button } from '../../components/Button'
import { useToast } from '../../components/Toast'
import {
  numberValue,
  recoveryTriggers,
  toRecoveryForm,
  type RecoveryForm,
} from './recoveryForm'
import styles from './VpnFailoverQuickPanel.module.css'

function toQuickInput(form: RecoveryForm): VpnRecoveryUpdateInput {
  return {
    enabled: form.enabled,
    autoConnectVpn: form.autoConnectVpn,
    autoResume: form.autoResume,
    handleQuotaExceeded: form.handleQuotaExceeded,
    handleRateLimited: form.handleRateLimited,
    handleAuthFailed: form.handleAuthFailed,
    handleModelUnavailable: form.handleModelUnavailable,
    handleNetwork: form.handleNetwork,
    handleStalled: form.handleStalled,
    handleUnknown: form.handleUnknown,
    continuationPrompt: form.continuationPrompt,
    cooldownSeconds: form.cooldownSeconds,
    maxAttemptsPerSession: form.maxAttemptsPerSession,
    stallThresholdSeconds: form.stallThresholdSeconds,
  }
}

export function VpnFailoverQuickPanel({ machineId }: { machineId: string }) {
  const toast = useToast()
  const recoveryQuery = useVpnRecovery(machineId)
  const update = useUpdateVpnRecovery(machineId)
  const recovery = recoveryQuery.data?.data
  const [draft, setDraft] = useState<{ key: string; form: RecoveryForm } | null>(null)
  const form =
    recovery === undefined
      ? null
      : draft?.key === recovery.updatedAt
        ? draft.form
        : toRecoveryForm(recovery)
  const setForm = (next: RecoveryForm) =>
    setDraft({ key: recovery?.updatedAt ?? '', form: next })

  const dirty =
    recovery !== undefined &&
    form !== null &&
    JSON.stringify(toQuickInput(form)) !==
      JSON.stringify(toQuickInput(toRecoveryForm(recovery)))

  const submit = () => {
    if (!form || !dirty) {
      return
    }
    update.mutate(toQuickInput(form), {
      onSuccess: (result) => {
        if (result.data.kind === 'pending') {
          toast.notify('info', '복구 설정 저장 명령을 접수했습니다.')
          return
        }
        if (result.data.command.status === 'Failed') {
          toast.notify(
            'error',
            result.data.command.errorMessage ?? '복구 설정을 저장하지 못했습니다.',
          )
          return
        }
        toast.notify('success', '복구 설정을 저장했습니다.')
      },
      onError: (error) => toast.notify('error', describeApiError(error).message),
    })
  }

  return (
    <section className={styles.panel} aria-label="오류 복구(failover) 설정">
      <div className={styles.header}>
        <div className={styles.heading}>
          <h3 className={styles.title}>오류 복구(failover)</h3>
          <p className={styles.subtitle}>
            선택한 오류가 발생하면 VPN을 전환하고 같은 세션을 재시작합니다. 설정은 이
            머신의 모든 세션에 적용됩니다.
          </p>
        </div>
        {recovery ? (
          <Badge tone={recovery.enabled ? 'ok' : 'neutral'}>
            {recovery.enabled ? '사용' : '중지'}
          </Badge>
        ) : null}
      </div>

      {recoveryQuery.isLoading ? (
        <p className={styles.message}>복구 설정을 불러오는 중…</p>
      ) : null}

      {recoveryQuery.isError ? (
        <p className={styles.error} role="alert">
          복구 설정을 불러오지 못했습니다: {describeApiError(recoveryQuery.error).message}
        </p>
      ) : null}

      {form ? (
        <div className={styles.form}>
          <div className={styles.toggles}>
            <label className={styles.toggle}>
              <input
                type="checkbox"
                checked={form.enabled}
                onChange={(event) => setForm({ ...form, enabled: event.target.checked })}
              />
              <span>오류 복구 사용</span>
            </label>
            <label className={styles.toggle}>
              <input
                type="checkbox"
                checked={form.autoConnectVpn}
                onChange={(event) =>
                  setForm({ ...form, autoConnectVpn: event.target.checked })
                }
              />
              <span>복구 시 자동 VPN 전환</span>
            </label>
            <label className={styles.toggle}>
              <input
                type="checkbox"
                checked={form.autoResume}
                onChange={(event) => setForm({ ...form, autoResume: event.target.checked })}
              />
              <span>복구 후 세션 재개</span>
            </label>
          </div>

          <fieldset className={styles.triggers}>
            <legend className={styles.legend}>재시작할 오류</legend>
            <div className={styles.triggerGrid}>
              {recoveryTriggers.map((trigger) => (
                <label key={trigger.key} className={styles.trigger}>
                  <input
                    type="checkbox"
                    checked={form[trigger.key]}
                    onChange={(event) =>
                      setForm({ ...form, [trigger.key]: event.target.checked })
                    }
                  />
                  <span className={styles.triggerText}>
                    <span>{trigger.label}</span>
                    <span className={styles.triggerHint}>{trigger.hint}</span>
                  </span>
                </label>
              ))}
            </div>
          </fieldset>

          {form.enabled ? (
            <label className={styles.prompt}>
              <span>재시작 프롬프트</span>
              <textarea
                rows={2}
                maxLength={4000}
                aria-label="재시작 프롬프트"
                value={form.continuationPrompt}
                onChange={(event) =>
                  setForm({ ...form, continuationPrompt: event.target.value })
                }
                placeholder="Continue from where you stopped."
              />
              <span className={styles.hint}>
                중단된 턴을 이어갈 지시문입니다. 빈 값은 기본 문구로 대체됩니다.
              </span>
            </label>
          ) : (
            <p className={styles.hint}>
              사용을 켜면 재시작에 사용할 프롬프트를 지정할 수 있습니다.
            </p>
          )}

          <details className={styles.advanced}>
            <summary className={styles.advancedSummary}>고급 설정</summary>
            <div className={styles.advancedGrid}>
              <label className={styles.numberField}>
                <span>재시도 cooldown(초)</span>
                <input
                  type="number"
                  min={0}
                  max={86_400}
                  value={form.cooldownSeconds}
                  onChange={(event) =>
                    setForm({
                      ...form,
                      cooldownSeconds: numberValue(
                        event.target.value,
                        form.cooldownSeconds,
                      ),
                    })
                  }
                />
              </label>
              <label className={styles.numberField}>
                <span>세션당 최대 시도</span>
                <input
                  type="number"
                  min={1}
                  max={10}
                  value={form.maxAttemptsPerSession}
                  onChange={(event) =>
                    setForm({
                      ...form,
                      maxAttemptsPerSession: numberValue(
                        event.target.value,
                        form.maxAttemptsPerSession,
                      ),
                    })
                  }
                />
              </label>
              <label className={styles.numberField}>
                <span>정체 임계값(초)</span>
                <input
                  type="number"
                  min={0}
                  max={86_400}
                  value={form.stallThresholdSeconds}
                  onChange={(event) =>
                    setForm({
                      ...form,
                      stallThresholdSeconds: numberValue(
                        event.target.value,
                        form.stallThresholdSeconds,
                      ),
                    })
                  }
                />
                <span className={styles.hint}>0이면 모든 정체 이벤트에 반응합니다.</span>
              </label>
            </div>
          </details>

          <div className={styles.actions}>
            <div className={styles.actionMeta}>
              <Link
                to={`/m/${encodeURIComponent(machineId)}/vpn`}
                className={styles.detailLink}
              >
                VPN 설정에서 상세 조정
                <ArrowUpRight size={13} aria-hidden="true" />
              </Link>
              {dirty ? (
                <span className={styles.dirty} role="status">
                  저장되지 않은 변경
                </span>
              ) : null}
            </div>
            <Button
              size="sm"
              tone="secondary"
              type="button"
              icon={<Save size={14} aria-hidden="true" />}
              loading={update.isPending}
              disabled={!dirty}
              onClick={submit}
            >
              복구 설정 저장
            </Button>
          </div>
        </div>
      ) : null}
    </section>
  )
}

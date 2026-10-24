import { useState, type FormEvent } from 'react'
import { RotateCcw, Save } from 'lucide-react'
import { describeApiError } from '../../api/errors'
import { formatRelative } from '../../api/format'
import { useUpdateVpnRecovery, useVpnRecovery } from '../../api/queries'
import { Badge } from '../../components/Badge'
import { Button } from '../../components/Button'
import { InlineNotice } from '../../components/InlineNotice'
import { useToast } from '../../components/Toast'
import {
  numberValue,
  recoveryTriggers as triggers,
  toRecoveryForm as toForm,
  toRecoveryInput as toInput,
  type RecoveryForm,
} from './recoveryForm'
import styles from './VpnRecoveryPanel.module.css'

export function VpnRecoveryPanel({ machineId }: { machineId: string }) {
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
        : toForm(recovery)
  const setForm = (next: RecoveryForm) =>
    setDraft({ key: recovery?.updatedAt ?? '', form: next })

  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (!form) {
      return
    }
    update.mutate(toInput(form), {
      onSuccess: (result) => {
        if (result.data.kind === 'pending') {
          toast.notify('info', '복구 설정 저장 명령을 접수했습니다.')
          return
        }
        if (result.data.command.status === 'Failed') {
          toast.notify('error', result.data.command.errorMessage ?? '복구 설정을 저장하지 못했습니다.')
          return
        }
        toast.notify('success', '복구 설정을 저장했습니다.')
      },
      onError: (error) => toast.notify('error', describeApiError(error).message),
    })
  }

  if (recoveryQuery.isLoading) {
    return (
      <InlineNotice tone="info" title="복구 설정을 불러오는 중입니다">
        잠시만 기다려 주세요.
      </InlineNotice>
    )
  }

  if (recoveryQuery.isError) {
    return (
      <InlineNotice tone="danger" title="복구 설정을 불러오지 못했습니다">
        {describeApiError(recoveryQuery.error).message}
      </InlineNotice>
    )
  }

  if (!recovery || !form) {
    return null
  }

  return (
    <section className={styles.panel}>
      <div className={styles.header}>
        <div>
          <p className={styles.eyebrow}>Recovery &amp; resume</p>
          <h2 className={styles.title}>오류 복구와 세션 재시작</h2>
          <p className={styles.subtitle}>
            선택한 오류가 발생하면 현재 턴을 중단하고 VPN을 전환한 뒤, 아래 프롬프트로 같은
            세션을 다시 시작합니다. 설정은 저장 즉시 적용되며 에이전트를 재시작할 필요가
            없습니다.
          </p>
        </div>
        <Badge tone={recovery.enabled ? 'ok' : 'neutral'}>
          {recovery.enabled ? '사용' : '중지'}
        </Badge>
      </div>

      <form className={styles.form} onSubmit={submit}>
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
            onChange={(event) => setForm({ ...form, autoConnectVpn: event.target.checked })}
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

        <fieldset className={styles.triggers}>
          <legend className={styles.legend}>재시작할 오류</legend>
          <div className={styles.triggerGrid}>
            {triggers.map((trigger) => (
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

        <label className={styles.fieldWide}>
          <span>재시작 프롬프트</span>
          <textarea
            rows={3}
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

        <label className={styles.field}>
          <span>국가</span>
          <input
            type="text"
            placeholder="JP"
            value={form.country}
            onChange={(event) => setForm({ ...form, country: event.target.value })}
          />
        </label>

        <label className={styles.field}>
          <span>도시</span>
          <input
            type="text"
            placeholder="Tokyo"
            value={form.city}
            onChange={(event) => setForm({ ...form, city: event.target.value })}
          />
        </label>

        <label className={styles.fieldWide}>
          <span>허용 서버</span>
          <input
            type="text"
            placeholder="JP#1, JP#4"
            value={form.servers}
            onChange={(event) => setForm({ ...form, servers: event.target.value })}
          />
          <span className={styles.hint}>비우면 정책이 선택한 서버를 사용합니다.</span>
        </label>

        <label className={styles.field}>
          <span>재시도 cooldown(초)</span>
          <input
            type="number"
            min={0}
            max={86_400}
            value={form.cooldownSeconds}
            onChange={(event) =>
              setForm({
                ...form,
                cooldownSeconds: numberValue(event.target.value, form.cooldownSeconds),
              })
            }
          />
        </label>

        <label className={styles.field}>
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

        <label className={styles.field}>
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

        <div className={styles.actions}>
          <span className={styles.updated}>
            {recovery.discordWebhookConfigured
              ? 'Discord webhook 설정됨'
              : 'Discord webhook 미설정 · Settings에서 지정'}{' '}
            · {formatRelative(recovery.updatedAt)} 갱신
          </span>
          <Button type="submit" size="sm" loading={update.isPending} icon={<Save size={14} />}>
            복구 설정 저장
          </Button>
        </div>
      </form>

      <p className={styles.footnote}>
        <RotateCcw size={13} aria-hidden="true" /> 재시작은 같은 세션에서 abort와 prompt를 순서대로
        수행합니다. 완료된 작업은 프롬프트에서 다시 반복하지 않도록 지시하세요.
      </p>
    </section>
  )
}

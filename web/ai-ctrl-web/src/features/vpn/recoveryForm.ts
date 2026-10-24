import type { VpnRecoveryUpdateInput, VpnRecoveryView } from '../../api/types'

export type RecoveryToggleKey =
  | 'handleQuotaExceeded'
  | 'handleRateLimited'
  | 'handleAuthFailed'
  | 'handleModelUnavailable'
  | 'handleNetwork'
  | 'handleStalled'
  | 'handleUnknown'

export type RecoveryForm = Record<RecoveryToggleKey, boolean> & {
  enabled: boolean
  autoConnectVpn: boolean
  autoResume: boolean
  continuationPrompt: string
  country: string
  city: string
  servers: string
  cooldownSeconds: number
  maxAttemptsPerSession: number
  stallThresholdSeconds: number
  discordEnabled: boolean
  discordNotifyOnFailureOnly: boolean
}

export const recoveryTriggers: Array<{
  key: RecoveryToggleKey
  label: string
  hint: string
}> = [
  {
    key: 'handleQuotaExceeded',
    label: '무료 할당량 소진',
    hint: 'free-token quota · 429',
  },
  {
    key: 'handleRateLimited',
    label: '요청 한도 초과',
    hint: 'rate limit · 429',
  },
  {
    key: 'handleAuthFailed',
    label: '인증 실패',
    hint: '401/403',
  },
  {
    key: 'handleModelUnavailable',
    label: '모델 사용 불가',
    hint: 'model unavailable',
  },
  {
    key: 'handleNetwork',
    label: '네트워크 오류',
    hint: 'retry / fetch failed',
  },
  {
    key: 'handleStalled',
    label: '응답 정체',
    hint: 'stalled threshold',
  },
  {
    key: 'handleUnknown',
    label: '알 수 없는 오류',
    hint: 'unknown',
  },
]

export function toRecoveryForm(recovery: VpnRecoveryView): RecoveryForm {
  return {
    enabled: recovery.enabled,
    autoConnectVpn: recovery.autoConnectVpn,
    autoResume: recovery.autoResume,
    handleQuotaExceeded: recovery.handleQuotaExceeded,
    handleRateLimited: recovery.handleRateLimited,
    handleAuthFailed: recovery.handleAuthFailed,
    handleModelUnavailable: recovery.handleModelUnavailable,
    handleNetwork: recovery.handleNetwork,
    handleStalled: recovery.handleStalled,
    handleUnknown: recovery.handleUnknown,
    continuationPrompt: recovery.continuationPrompt,
    country: recovery.country ?? '',
    city: recovery.city ?? '',
    servers: recovery.servers.join(', '),
    cooldownSeconds: recovery.cooldownSeconds,
    maxAttemptsPerSession: recovery.maxAttemptsPerSession,
    stallThresholdSeconds: recovery.stallThresholdSeconds,
    discordEnabled: recovery.discordEnabled,
    discordNotifyOnFailureOnly: recovery.discordNotifyOnFailureOnly,
  }
}

export function toRecoveryInput(form: RecoveryForm): VpnRecoveryUpdateInput {
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
    country: form.country.trim(),
    city: form.city.trim(),
    servers: form.servers
      .split(',')
      .map((value) => value.trim())
      .filter(Boolean),
    cooldownSeconds: form.cooldownSeconds,
    maxAttemptsPerSession: form.maxAttemptsPerSession,
    stallThresholdSeconds: form.stallThresholdSeconds,
    discordEnabled: form.discordEnabled,
    discordNotifyOnFailureOnly: form.discordNotifyOnFailureOnly,
  }
}

export function numberValue(value: string, fallback: number): number {
  const parsed = Number(value)
  return Number.isFinite(parsed) ? Math.trunc(parsed) : fallback
}

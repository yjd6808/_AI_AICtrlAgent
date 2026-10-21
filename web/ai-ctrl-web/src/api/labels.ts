import type {
  AcRunState,
  CommandStatus,
  ErrorClass,
  FeatureSection,
  InstanceMode,
  ProviderKind,
  SessionDisplayState,
} from './types'

export const displayStateLabels: Record<SessionDisplayState, string> = {
  Running: '실행 중',
  Retrying: '재시도',
  WaitingPermission: '권한 대기',
  Stalled: '멈춤 의심',
  QuotaBlocked: '토큰 부족',
  Errored: '오류',
  ForeignBusy: '다른 앱 실행',
  Aborted: '중단됨',
  Idle: '대기',
  Unknown: '알 수 없음',
}

export const displayStateDescriptions: Record<SessionDisplayState, string> = {
  Running: '에이전트가 현재 턴을 실행 중입니다.',
  Retrying: '오류 후 재시도 중입니다.',
  WaitingPermission: '도구 실행 권한 승인을 기다리고 있습니다.',
  Stalled: '실행 중이지만 10분 이상 새 출력이 없습니다.',
  QuotaBlocked: '할당량 또는 속도 제한으로 막혀 있습니다.',
  Errored: '마지막 assistant 메시지에 오류가 있습니다.',
  ForeignBusy: '다른 앱이 이 세션을 처리하고 있는 것으로 추정됩니다.',
  Aborted: '마지막 턴이 중단되었습니다.',
  Idle: '대기 중입니다.',
  Unknown: '표시 상태를 확인할 수 없습니다.',
}

export const commandStatusLabels: Record<CommandStatus, string> = {
  Created: '생성됨',
  Queued: '대기열',
  Dispatched: '전달됨',
  Acknowledged: '확인됨',
  Running: '진행 중',
  Succeeded: '성공',
  Failed: '실패',
  Cancelled: '취소됨',
  Expired: '만료됨',
  Unknown: '알 수 없음',
}

export const runStateLabels: Record<AcRunState, string> = {
  Unknown: '알 수 없음',
  Idle: '대기',
  Busy: '실행 중',
  Retrying: '재시도',
  WaitingPermission: '권한 대기',
  Errored: '오류',
  ForeignBusy: '다른 앱 실행',
}

export const errorClassLabels: Record<ErrorClass, string> = {
  None: '없음',
  QuotaExceeded: '할당량 초과',
  RateLimited: '속도 제한',
  AuthFailed: '인증 실패',
  ModelUnavailable: '모델 사용 불가',
  Network: '네트워크',
  Aborted: '중단됨',
  ToolError: '도구 오류',
  Unknown: '알 수 없음',
}

export const providerLabels: Record<ProviderKind, string> = {
  OpenCode: 'OpenCode',
  ClaudeCode: 'Claude Code',
}

export const modeLabels: Record<InstanceMode, string> = {
  Managed: '관리형',
  SharedStore: '공유 저장소',
}

export const sectionLabels: Record<FeatureSection, string> = {
  Agents: 'AI Agents',
  Workspace: 'Workspace',
  Etc: '기타',
}

export const commandStatusTone: Record<CommandStatus, string> = {
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

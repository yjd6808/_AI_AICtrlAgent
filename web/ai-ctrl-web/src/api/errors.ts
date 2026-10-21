import type { Problem } from './types'

export class ApiError extends Error {
  readonly status: number
  readonly code: string
  readonly detail: string | null
  readonly title: string
  readonly instance: string | null
  readonly traceId: string | null

  constructor(
    status: number,
    code: string,
    detail: string | null,
    traceId: string | null,
    title = 'Request failed',
    instance: string | null = null,
  ) {
    super(detail ?? title)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.detail = detail
    this.title = title
    this.instance = instance
    this.traceId = traceId
  }

  static fromProblem(problem: Problem, traceId: string | null): ApiError {
    return new ApiError(
      problem.status,
      problem.code,
      problem.detail,
      traceId,
      problem.title,
      problem.instance,
    )
  }
}

export type ErrorDescription = {
  title: string
  message: string
  hint: string | null
  traceId: string | null
  code: string | null
  status: number | null
}

const codeMessages: Record<string, { title: string; message: string; hint?: string }> = {
  machine_offline: {
    title: '머신이 오프라인입니다',
    message: '에이전트가 서버에 연결되어 있지 않습니다.',
    hint: '머신의 Client가 실행 중인지 확인한 뒤 새로고침하세요.',
  },
  agent_no_ack: {
    title: '에이전트 응답 없음',
    message: '명령을 전달했지만 에이전트가 확인 응답을 보내지 않았습니다.',
    hint: '잠시 후 명령 이력에서 결과를 확인하세요.',
  },
  machine_not_found: {
    title: '머신을 찾을 수 없습니다',
    message: '머신이 삭제되었거나 등록이 해제되었습니다.',
  },
  session_not_found: {
    title: '세션을 찾을 수 없습니다',
    message: '세션이 삭제되었거나 인스턴스에서 더 이상 보이지 않습니다.',
  },
  instance_not_found: {
    title: '인스턴스를 찾을 수 없습니다',
    message: '에이전트 인스턴스가 종료되었거나 감지되지 않았습니다.',
  },
  command_not_found: {
    title: '명령을 찾을 수 없습니다',
    message: '명령 기록이 존재하지 않습니다.',
  },
  query_timeout: {
    title: '응답 시간 초과',
    message: '에이전트가 제한 시간 안에 응답하지 않았습니다.',
    hint: '네트워크 상태와 Client 로그를 확인하세요.',
  },
  abort_timeout: {
    title: '중단 시간 초과',
    message: '세션을 중단했지만 유휴 상태가 확인되지 않았습니다.',
  },
  not_settled: {
    title: '턴이 정리되지 않았습니다',
    message: '마지막 assistant 메시지가 완료 또는 오류 상태가 아닙니다.',
  },
  prompt_unconfirmed: {
    title: '프롬프트 수락 미확인',
    message: '프롬프트 전송은 요청되었지만 수락이 확인되지 않았습니다.',
    hint: '중복 전송을 피하기 위해 자동 재시도하지 않습니다.',
  },
  foreign_turn: {
    title: '다른 앱에서 실행 중',
    message: 'Desktop 등 다른 서버가 이 세션의 턴을 처리하고 있습니다.',
    hint: 'Desktop에서 중단하거나 Managed 모드로 전환하세요.',
  },
  busy: {
    title: '세션이 사용 중입니다',
    message: '다른 명령이 이 세션에서 진행 중입니다.',
  },
  capability_missing: {
    title: '지원하지 않는 기능',
    message: '이 인스턴스는 요청한 기능을 지원하지 않습니다.',
  },
  geo_blocked: {
    title: '접속이 차단되었습니다',
    message: '허용되지 않은 지역에서 접속했습니다.',
  },
  email_not_allowed: {
    title: '허용되지 않은 계정입니다',
    message: '이 Google 계정은 서버 허용 목록에 없습니다.',
  },
  auth_unavailable: {
    title: '인증을 사용할 수 없습니다',
    message: '서버에 Google 인증이 설정되지 않았습니다.',
  },
  duplicate_command: {
    title: '중복 명령',
    message: '같은 요청이 이미 처리되었습니다.',
  },
  server_restarting: {
    title: '서버 재시작 중',
    message: '서버가 재시작되고 있습니다. 잠시 후 다시 시도하세요.',
  },
  internal_error: {
    title: '서버 오류',
    message: '서버에서 예상하지 못한 오류가 발생했습니다.',
  },
}

export function describeApiError(error: unknown): ErrorDescription {
  if (error instanceof ApiError) {
    const mapped = codeMessages[error.code]
    if (mapped) {
      return {
        title: mapped.title,
        message: error.detail ?? mapped.message,
        hint: mapped.hint ?? null,
        traceId: error.traceId,
        code: error.code,
        status: error.status,
      }
    }

    if (error.status === 401) {
      return {
        title: '로그인이 필요합니다',
        message: error.detail ?? '세션이 만료되었습니다. 다시 로그인하세요.',
        hint: null,
        traceId: error.traceId,
        code: error.code,
        status: error.status,
      }
    }

    if (error.status === 403) {
      return {
        title: '접근이 거부되었습니다',
        message: error.detail ?? '이 작업을 수행할 권한이 없습니다.',
        hint: null,
        traceId: error.traceId,
        code: error.code,
        status: error.status,
      }
    }

    if (error.status === 404) {
      return {
        title: '대상을 찾을 수 없습니다',
        message: error.detail ?? '요청한 리소스가 존재하지 않습니다.',
        hint: null,
        traceId: error.traceId,
        code: error.code,
        status: error.status,
      }
    }

    if (error.status === 429) {
      return {
        title: '요청이 너무 많습니다',
        message: error.detail ?? '잠시 후 다시 시도하세요.',
        hint: null,
        traceId: error.traceId,
        code: error.code,
        status: error.status,
      }
    }

    if (error.status >= 500) {
      return {
        title: '서버 또는 에이전트 오류',
        message: error.detail ?? error.message,
        hint: '잠시 후 다시 시도하고 trace ID를 확인하세요.',
        traceId: error.traceId,
        code: error.code,
        status: error.status,
      }
    }

    return {
      title: error.title,
      message: error.detail ?? error.message,
      hint: null,
      traceId: error.traceId,
      code: error.code,
      status: error.status,
    }
  }

  if (error instanceof TypeError) {
    return {
      title: '서버에 연결할 수 없습니다',
      message: error.message,
      hint: '네트워크 연결과 서버 상태를 확인하세요.',
      traceId: null,
      code: null,
      status: null,
    }
  }

  if (error instanceof Error) {
    return {
      title: '요청을 완료하지 못했습니다',
      message: error.message,
      hint: null,
      traceId: null,
      code: null,
      status: null,
    }
  }

  return {
    title: '요청을 완료하지 못했습니다',
    message: String(error),
    hint: null,
    traceId: null,
    code: null,
    status: null,
  }
}

export function isUnauthorized(error: unknown): boolean {
  return error instanceof ApiError && error.status === 401
}

export function isMachineOffline(error: unknown): boolean {
  return error instanceof ApiError && error.code === 'machine_offline'
}

export function isForbiddenCode(error: unknown, code: string): boolean {
  return error instanceof ApiError && error.status === 403 && error.code === code
}

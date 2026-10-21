import { HttpResponse, http } from 'msw'
import type { ApiEnvelope, ModelRef, SessionView } from '../api/types'
import {
  advanceCommand,
  getMockState,
  liveMeta,
  type MockCommand,
  type MockState,
} from './fixtures'

const API = '/api/v1'

function envelope<T>(state: MockState, data: T, machineId?: string): ApiEnvelope<T> {
  return { data, meta: liveMeta(state, machineId) }
}

function problem(
  status: number,
  code: string,
  title: string,
  detail: string | null = null,
  instance: string | null = null,
) {
  return HttpResponse.json(
    {
      type: `https://ai-ctrl.local/problems/${code}`,
      title,
      status,
      detail,
      instance,
      code,
    },
    {
      status,
      headers: { 'X-Trace-Id': `trace_${Math.random().toString(16).slice(2, 10)}` },
    },
  )
}

function notFound(code: string, title: string, detail: string) {
  return problem(404, code, title, detail)
}

function machineOf(state: MockState, machineId: string) {
  return state.machines.find((machine) => machine.id.value === machineId)
}

function sessionOf(state: MockState, sessionId: string) {
  return state.sessions[sessionId]
}

function startCommand(
  state: MockState,
  requestId: string,
  machineId: string,
  type: string,
  mode: MockCommand['mode'],
  sessionId: string | null,
  promptText: string | null,
  outcome: MockCommand['outcome'],
  delayMs: number,
): MockCommand {
  const now = Date.now()
  const command: MockCommand = {
    machineId,
    startedAt: now,
    completionDelayMs: delayMs,
    outcome,
    sessionId,
    promptText,
    mode,
    completed: false,
    detail: {
      commandId: requestId,
      type,
      status: 'Queued',
      result: null,
      errorCode: null,
      errorMessage: null,
      issuedBy: 'jdyun@example.com',
      issuedAt: new Date(now).toISOString(),
      updatedAt: new Date(now).toISOString(),
      steps: [
        {
          seq: 1,
          step: 'accepted',
          message: '서버가 명령을 접수했습니다.',
          at: new Date(now).toISOString(),
        },
      ],
    },
  }
  state.commands[requestId] = command
  return command
}

function pending(command: MockCommand) {
  return {
    requestId: command.detail.commandId,
    status: command.detail.status,
    lastStep: command.detail.steps[command.detail.steps.length - 1]?.step ?? null,
  }
}

function requestIdOf(request: Request): string {
  return (
    request.headers.get('X-Request-Id') ??
    `cmd_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`
  )
}

type PromptBody = {
  text?: string
  model?: ModelRef | null
  agent?: string | null
}

export const handlers = [
  http.get(`${API}/auth/me`, () =>
    HttpResponse.json({
      email: 'jdyun@example.com',
      name: 'JD Yun',
      authTime: new Date(Date.now() - 42 * 60_000).toISOString(),
    }),
  ),

  http.get(`${API}/machines`, () => {
    const state = getMockState()
    return HttpResponse.json(envelope(state, state.machines))
  }),

  http.post(`${API}/machines/enrollment-codes`, () => {
    const state = getMockState()
    const code = `AC-${Math.random().toString(36).slice(2, 6).toUpperCase()}-${Math.random()
      .toString(36)
      .slice(2, 6)
      .toUpperCase()}`
    state.enrollmentCodes.push(code)
    return HttpResponse.json(
      { code, expiresAt: new Date(Date.now() + 10 * 60_000).toISOString() },
      { status: 201 },
    )
  }),

  http.get(`${API}/machines/:machineId/instances`, ({ params }) => {
    const state = getMockState()
    const machineId = String(params.machineId)
    if (!machineOf(state, machineId)) {
      return notFound('machine_not_found', '머신을 찾을 수 없습니다', machineId)
    }
    return HttpResponse.json(envelope(state, state.instances[machineId] ?? [], machineId))
  }),

  http.get(`${API}/machines/:machineId/features`, ({ params }) => {
    const state = getMockState()
    const machineId = String(params.machineId)
    if (!machineOf(state, machineId)) {
      return notFound('machine_not_found', '머신을 찾을 수 없습니다', machineId)
    }
    return HttpResponse.json(envelope(state, state.features[machineId] ?? [], machineId))
  }),

  http.get(`${API}/machines/:machineId/models`, ({ params, request }) => {
    const state = getMockState()
    const machineId = String(params.machineId)
    const instanceId = new URL(request.url).searchParams.get('instance')
    return HttpResponse.json(
      envelope(state, state.models[instanceId ?? ''] ?? state.models.inst_alpha_opencode, machineId),
    )
  }),

  http.post(`${API}/machines/:machineId/instances/refresh`, ({ params, request }) => {
    const state = getMockState()
    const machineId = String(params.machineId)
    if (!machineOf(state, machineId)) {
      return notFound('machine_not_found', '머신을 찾을 수 없습니다', machineId)
    }
    const command = startCommand(
      state,
      requestIdOf(request),
      machineId,
      'machine.instances.refresh',
      'refresh',
      null,
      null,
      'succeeded',
      900,
    )
    return HttpResponse.json(envelope(state, pending(command), machineId), { status: 202 })
  }),

  http.get(`${API}/machines/:machineId/sessions`, ({ params, request }) => {
    const state = getMockState()
    const machineId = String(params.machineId)
    if (!machineOf(state, machineId)) {
      return notFound('machine_not_found', '머신을 찾을 수 없습니다', machineId)
    }
    const url = new URL(request.url)
    const instanceId = url.searchParams.get('instance')
    const view = (url.searchParams.get('view') ?? 'all') as SessionView
    const rows = Object.values(state.sessions).filter((sessionValue) => {
      if (sessionValue.ref.machine.value !== machineId) {
        return false
      }
      if (instanceId && sessionValue.ref.instance.value !== instanceId) {
        return false
      }
      if (view === 'active') {
        return sessionValue.activeOnInstance
      }
      return true
    })
    return HttpResponse.json(
      envelope(state, { sessions: rows, nextCursor: null, hasMore: false }, machineId),
    )
  }),

  http.get(`${API}/machines/:machineId/sessions/:sessionId`, ({ params }) => {
    const state = getMockState()
    const machineId = String(params.machineId)
    const sessionId = String(params.sessionId)
    const value = sessionOf(state, sessionId)
    if (!value || value.ref.machine.value !== machineId) {
      return notFound('session_not_found', '세션을 찾을 수 없습니다', sessionId)
    }
    return HttpResponse.json(envelope(state, value, machineId))
  }),

  http.get(`${API}/machines/:machineId/sessions/:sessionId/messages`, ({ params, request }) => {
    const state = getMockState()
    const machineId = String(params.machineId)
    const sessionId = String(params.sessionId)
    if (!sessionOf(state, sessionId)) {
      return notFound('session_not_found', '세션을 찾을 수 없습니다', sessionId)
    }
    const url = new URL(request.url)
    const limit = Math.max(1, Math.min(200, Number(url.searchParams.get('limit') ?? 50)))
    const cursor = Number(url.searchParams.get('cursor') ?? 0)
    const offset = Number.isFinite(cursor) ? cursor : 0
    const all = state.messages[sessionId] ?? []
    const page = all.slice(offset, offset + limit)
    const nextOffset = offset + page.length
    const hasMore = nextOffset < all.length
    return HttpResponse.json(
      envelope(
        state,
        {
          messages: page,
          nextCursor: hasMore ? String(nextOffset) : null,
          hasMore,
        },
        machineId,
      ),
    )
  }),

  http.post(`${API}/machines/:machineId/sessions/:sessionId/prompt`, async ({ params, request }) => {
    const state = getMockState()
    const machineId = String(params.machineId)
    const sessionId = String(params.sessionId)
    const machine = machineOf(state, machineId)
    if (!machine) {
      return notFound('machine_not_found', '머신을 찾을 수 없습니다', machineId)
    }
    if (!machine.online) {
      return problem(503, 'machine_offline', '머신이 오프라인입니다', machine.hostname)
    }
    const target = sessionOf(state, sessionId)
    if (!target) {
      return notFound('session_not_found', '세션을 찾을 수 없습니다', sessionId)
    }
    const instanceId = target.ref.instance.value
    const instance = (state.instances[machineId] ?? []).find(
      (item) => item.id.value === instanceId,
    )
    if (!instance?.managed) {
      return problem(
        403,
        'capability_missing',
        '지원하지 않는 기능',
        'SharedStore 인스턴스는 읽기 전용입니다.',
      )
    }
    const body = (await request.json()) as PromptBody
    const text = body.text?.trim() ?? ''
    if (!text) {
      return problem(400, 'invalid_request', '프롬프트가 비어 있습니다', 'text가 필요합니다.')
    }
    const command = startCommand(
      state,
      requestIdOf(request),
      machineId,
      'session.prompt',
      'prompt',
      sessionId,
      text,
      'succeeded',
      1_900,
    )
    target.displayState = 'Running'
    target.runState = 'Busy'
    target.busySince = new Date().toISOString()
    target.updatedAt = new Date().toISOString()
    return HttpResponse.json(envelope(state, pending(command), machineId), { status: 202 })
  }),

  http.post(
    `${API}/machines/:machineId/sessions/:sessionId/abort`,
    ({ params, request }) => {
      const state = getMockState()
      const machineId = String(params.machineId)
      const sessionId = String(params.sessionId)
      const machine = machineOf(state, machineId)
      if (!machine) {
        return notFound('machine_not_found', '머신을 찾을 수 없습니다', machineId)
      }
      if (!machine.online) {
        return problem(503, 'machine_offline', '머신이 오프라인입니다', machine.hostname)
      }
      const target = sessionOf(state, sessionId)
      if (!target) {
        return notFound('session_not_found', '세션을 찾을 수 없습니다', sessionId)
      }
      const command = startCommand(
        state,
        requestIdOf(request),
        machineId,
        'session.abort',
        'abort',
        sessionId,
        null,
        'succeeded',
        900,
      )
      return HttpResponse.json(envelope(state, pending(command), machineId), { status: 202 })
    },
  ),

  http.post(
    `${API}/machines/:machineId/sessions/:sessionId/abort-and-prompt`,
    async ({ params, request }) => {
      const state = getMockState()
      const machineId = String(params.machineId)
      const sessionId = String(params.sessionId)
      const machine = machineOf(state, machineId)
      if (!machine) {
        return notFound('machine_not_found', '머신을 찾을 수 없습니다', machineId)
      }
      if (!machine.online) {
        return problem(503, 'machine_offline', '머신이 오프라인입니다', machine.hostname)
      }
      const target = sessionOf(state, sessionId)
      if (!target) {
        return notFound('session_not_found', '세션을 찾을 수 없습니다', sessionId)
      }
      const body = (await request.json()) as PromptBody
      const text = body.text?.trim() ?? ''
      if (!text) {
        return problem(400, 'invalid_request', '프롬프트가 비어 있습니다', 'text가 필요합니다.')
      }
      const command = startCommand(
        state,
        requestIdOf(request),
        machineId,
        'session.abort-and-prompt',
        'abort-and-prompt',
        sessionId,
        text,
        'succeeded',
        2_200,
      )
      return HttpResponse.json(envelope(state, pending(command), machineId), { status: 202 })
    },
  ),

  http.post(
    `${API}/machines/:machineId/sessions/:sessionId/permissions/:requestId`,
    ({ params }) =>
      notFound(
        'capability_missing',
        '권한 응답을 지원하지 않습니다',
        `permission ${String(params.requestId)}`,
      ),
  ),

  http.get(`${API}/commands`, ({ request }) => {
    const state = getMockState()
    const url = new URL(request.url)
    const machineId = url.searchParams.get('machineId')
    const status = url.searchParams.get('status')
    const limit = Math.max(1, Math.min(200, Number(url.searchParams.get('limit') ?? 100)))
    Object.values(state.commands).forEach((command) => advanceCommand(state, command))
    const rows = Object.values(state.commands)
      .filter((command) => !machineId || command.machineId === machineId)
      .filter((command) => !status || command.detail.status === status)
      .sort((left, right) => right.startedAt - left.startedAt)
      .slice(0, limit)
      .map((command) => command.detail)
    return HttpResponse.json(envelope(state, rows))
  }),

  http.get(`${API}/commands/:commandId`, ({ params }) => {
    const state = getMockState()
    const command = state.commands[String(params.commandId)]
    if (!command) {
      return notFound('command_not_found', '명령을 찾을 수 없습니다', String(params.commandId))
    }
    advanceCommand(state, command)
    return HttpResponse.json(envelope(state, command.detail, command.machineId))
  }),

  http.get(`${API}/audit`, ({ request }) => {
    const state = getMockState()
    const url = new URL(request.url)
    const from = url.searchParams.get('from')
    const to = url.searchParams.get('to')
    const limit = Math.max(1, Math.min(500, Number(url.searchParams.get('limit') ?? 100)))
    const rows = state.audit
      .filter((entry) => !from || entry.at >= from)
      .filter((entry) => !to || entry.at <= to)
      .slice(0, limit)
    return HttpResponse.json(envelope(state, rows))
  }),

  http.get(`${API}/logs/server`, () => {
    const state = getMockState()
    return HttpResponse.json(
      envelope(
        state,
        Object.values(state.logs)
          .map((entry) => entry.file)
          .sort((left, right) => right.modifiedAt.localeCompare(left.modifiedAt)),
      ),
    )
  }),

  http.get(`${API}/logs/server/:name`, ({ params, request }) => {
    const state = getMockState()
    const name = String(params.name)
    const entry = state.logs[name]
    if (!entry) {
      return notFound('log_not_found', '로그 파일을 찾을 수 없습니다', name)
    }
    const url = new URL(request.url)
    const lines = Math.max(1, Math.min(5_000, Number(url.searchParams.get('lines') ?? 500)))
    const all = entry.content.split('\n')
    const content = all.slice(-lines).join('\n')
    return HttpResponse.json(
      envelope(state, {
        name,
        content,
        truncated: all.length > lines,
      }),
    )
  }),

  http.get(`${API}/machines/:machineId`, ({ params }) => {
    const state = getMockState()
    const machine = machineOf(state, String(params.machineId))
    if (!machine) {
      return notFound('machine_not_found', '머신을 찾을 수 없습니다', String(params.machineId))
    }
    return HttpResponse.json(envelope(state, machine, machine.id.value))
  }),

  http.delete(`${API}/machines/:machineId`, ({ params }) => {
    const state = getMockState()
    const machineId = String(params.machineId)
    const index = state.machines.findIndex((machine) => machine.id.value === machineId)
    if (index < 0) {
      return notFound('machine_not_found', '머신을 찾을 수 없습니다', machineId)
    }
    state.machines.splice(index, 1)
    delete state.instances[machineId]
    delete state.features[machineId]
    return new HttpResponse(null, { status: 204 })
  }),
]

import type {
  ApiMeta,
  AuditLogView,
  CommandDetailView,
  CommandStepView,
  InstanceInfo,
  LogFileView,
  MachineFeature,
  MachineInfo,
  MessageView,
  ModelOption,
  PartView,
  SessionDisplayState,
  SessionSummary,
} from '../api/types'

const minute = 60_000
const hour = 60 * minute

function meta(now: number, overrides: Partial<ApiMeta> = {}): ApiMeta {
  return {
    source: 'live',
    asOf: new Date(now).toISOString(),
    machineOnline: true,
    stale: false,
    latencyMs: 23,
    traceId: `trace_${Math.random().toString(16).slice(2, 10)}`,
    ...overrides,
  }
}

function feature(
  key: string,
  section: MachineFeature['section'],
  title: string,
  route: string,
  available = true,
  unavailableReason: string | null = null,
  badge: string | null = null,
): MachineFeature {
  return { key, section, title, route, available, unavailableReason, badge }
}

const openCode: InstanceInfo = {
  id: { value: 'inst_alpha_opencode' },
  kind: 'OpenCode',
  endpoint: 'http://127.0.0.1:4096',
  version: '1.18.2',
  mode: 'Managed',
  healthy: true,
  managed: true,
  capabilities: 'prompt,abort,abortAndPrompt,models,messages,sessions',
}

const claudeCode: InstanceInfo = {
  id: { value: 'inst_alpha_claude' },
  kind: 'ClaudeCode',
  endpoint: 'http://127.0.0.1:4310',
  version: '2.9.0',
  mode: 'SharedStore',
  healthy: true,
  managed: false,
  capabilities: 'models,messages,sessions',
}

const betaOpenCode: InstanceInfo = {
  id: { value: 'inst_beta_opencode' },
  kind: 'OpenCode',
  endpoint: 'http://127.0.0.1:4097',
  version: '1.18.2',
  mode: 'Managed',
  healthy: true,
  managed: true,
  capabilities: 'prompt,abort,abortAndPrompt,models,messages,sessions',
}

const models: ModelOption[] = [
  {
    providerId: 'anthropic',
    modelId: 'claude-sonnet-4-5',
    name: 'Claude Sonnet 4.5',
  },
  {
    providerId: 'anthropic',
    modelId: 'claude-opus-4-1',
    name: 'Claude Opus 4.1',
  },
  {
    providerId: 'openai',
    modelId: 'gpt-5.2-codex',
    name: 'GPT-5.2 Codex',
  },
]

function session(
  now: number,
  machineId: string,
  instanceId: string,
  sessionId: string,
  title: string,
  displayState: SessionDisplayState,
  offsetMinutes: number,
  extra: Partial<SessionSummary> = {},
): SessionSummary {
  const updatedAt = new Date(now - offsetMinutes * minute).toISOString()
  const active =
    displayState === 'Running' ||
    displayState === 'Retrying' ||
    displayState === 'WaitingPermission' ||
    displayState === 'Stalled'
  const runState =
    displayState === 'Running'
      ? 'Busy'
      : displayState === 'Retrying'
        ? 'Retrying'
        : displayState === 'WaitingPermission'
          ? 'WaitingPermission'
          : displayState === 'Errored'
            ? 'Errored'
            : displayState === 'ForeignBusy'
              ? 'ForeignBusy'
              : 'Idle'
  return {
    ref: { machine: { value: machineId }, instance: { value: instanceId }, sessionId },
    title,
    parentId: null,
    directory: 'E:\\Programming\\Mabi\\Tooling',
    runState,
    lastError:
      displayState === 'QuotaBlocked'
        ? 'QuotaExceeded'
        : displayState === 'Errored'
          ? 'ToolError'
          : displayState === 'Aborted'
            ? 'Aborted'
            : 'None',
    lastErrorMessage:
      displayState === 'QuotaBlocked'
        ? '주간 사용량 한도에 도달했습니다. 10월 24일 이후 다시 시도할 수 있습니다.'
        : displayState === 'Errored'
          ? 'apply_patch 도구가 파일 충돌로 실패했습니다.'
          : displayState === 'Aborted'
            ? '사용자가 이전 턴을 중단했습니다.'
            : null,
    updatedAt,
    lastModel: { providerId: 'anthropic', modelId: 'claude-sonnet-4-5' },
    retry:
      displayState === 'Retrying'
        ? {
            attempt: 2,
            message: '업스트림 429 응답, 12초 후 재시도합니다.',
            nextAt: new Date(now + 12_000).toISOString(),
          }
        : null,
    displayState,
    busySince: active ? new Date(now - (offsetMinutes + 2) * minute).toISOString() : null,
    lastActivityAt: updatedAt,
    messageCount: displayState === 'Running' ? 60 : 4,
    activeOnInstance: machineId !== 'mach_gamma',
    ...extra,
  }
}

function toolPart(now: number, index: number): PartView {
  return {
    partId: `part_tool_${index}`,
    type: 'tool',
    text: null,
    tool: {
      tool: index % 2 === 0 ? 'bash' : 'read',
      status: 'completed',
      title: index % 2 === 0 ? 'dotnet test' : 'SessionPage.tsx 읽기',
      outputPreview:
        index % 2 === 0
          ? 'Passed 128, Failed 0, Skipped 2'
          : '프롬프트 전송과 중단 흐름을 확인했습니다.',
      input: index % 2 === 0 ? '{"command":"dotnet test"}' : null,
      startedAt: new Date(now - 90_000).toISOString(),
      completedAt: new Date(now - 45_000).toISOString(),
    },
  }
}

function messagesFor(
  now: number,
  sessionId: string,
  count: number,
  title: string,
  errored: boolean,
): MessageView[] {
  const result: MessageView[] = []
  for (let index = 0; index < count; index += 1) {
    const isUser = index % 2 === 0
    const createdAt = new Date(now - (count - index) * 45_000).toISOString()
    const parts: PartView[] = [
      {
        partId: `part_${sessionId}_${index}`,
        type: 'text',
        text: isUser
          ? `${title} 작업을 이어서 진행하고 변경 사항을 요약해 주세요. (${Math.floor(index / 2) + 1})`
          : `변경 사항을 확인했습니다. ${index % 4 === 0 ? '테스트를 실행하고 결과를 정리하는 중입니다.' : '필요한 파일을 읽고 다음 단계를 준비했습니다.'}`,
        tool: null,
      },
    ]
    if (!isUser && index % 6 === 1) {
      parts.push(toolPart(now, index))
    }
    const last = index === count - 1
    result.push({
      messageId: `${sessionId}_m${index}`,
      role: isUser ? 'user' : 'assistant',
      createdAt,
      completedAt: new Date(new Date(createdAt).getTime() + 20_000).toISOString(),
      model: isUser ? null : { providerId: 'anthropic', modelId: 'claude-sonnet-4-5' },
      parts,
      error:
        errored && last
          ? {
              class: 'ToolError',
              name: 'ToolError',
              message: 'apply_patch 도구가 파일 충돌로 실패했습니다.',
              statusCode: 409,
              rawJson: '{"tool":"apply_patch","code":"conflict"}',
            }
          : null,
    })
  }
  return result
}

function step(seq: number, name: string, at: number, message: string | null = null): CommandStepView {
  return { seq, step: name, message, at: new Date(at).toISOString() }
}

export type MockCommand = {
  machineId: string
  startedAt: number
  completionDelayMs: number | null
  detail: CommandDetailView
  outcome: 'succeeded' | 'failed' | 'cancelled' | null
  sessionId: string | null
  promptText: string | null
  mode: 'prompt' | 'abort' | 'abort-and-prompt' | 'refresh' | 'seed'
  completed: boolean
}

export type MockState = {
  now: number
  machines: MachineInfo[]
  instances: Record<string, InstanceInfo[]>
  features: Record<string, MachineFeature[]>
  sessions: Record<string, SessionSummary>
  messages: Record<string, MessageView[]>
  models: Record<string, ModelOption[]>
  commands: Record<string, MockCommand>
  audit: AuditLogView[]
  logs: Record<string, { file: LogFileView; content: string }>
  enrollmentCodes: string[]
}

function seedCommand(
  now: number,
  commandId: string,
  type: string,
  status: CommandDetailView['status'],
  offset: number,
  machineId: string,
  result: unknown,
  error: { code: string; message: string } | null = null,
): MockCommand {
  const at = now - offset
  return {
    machineId,
    startedAt: at,
    completionDelayMs: null,
    outcome: null,
    sessionId: null,
    promptText: null,
    mode: 'seed',
    completed: true,
    detail: {
      commandId,
      type,
      status,
      result,
      errorCode: error?.code ?? null,
      errorMessage: error?.message ?? null,
      issuedBy: 'jdyun@example.com',
      issuedAt: new Date(at).toISOString(),
      updatedAt: new Date(at + 4_000).toISOString(),
      steps: [
        step(1, 'accepted', at, '서버가 명령을 접수했습니다.'),
        step(2, 'dispatched', at + 900, 'Client로 명령을 전달했습니다.'),
        step(
          3,
          status === 'Succeeded' ? 'completed' : 'finished',
          at + 4_000,
          status === 'Succeeded' ? '명령이 완료되었습니다.' : '명령이 종료되었습니다.',
        ),
      ],
    },
  }
}

export function createMockState(): MockState {
  const now = Date.now()
  const sessions: Record<string, SessionSummary> = {}
  const openAlpha = 'inst_alpha_opencode'
  const claudeAlpha = 'inst_alpha_claude'

  const definitions: Array<
    [string, string, SessionDisplayState, number, Partial<SessionSummary>?]
  > = [
    ['ses_running', 'M5 프론트엔드 세션 제어 구현', 'Running', 1],
    ['ses_retrying', 'M4 계약 테스트 보강', 'Retrying', 4],
    ['ses_waiting', '파일 업로드 권한 흐름 검토', 'WaitingPermission', 7],
    ['ses_stalled', '장시간 빌드 로그 분석', 'Stalled', 13],
    ['ses_quota', '대규모 리팩터링 계획', 'QuotaBlocked', 26],
    ['ses_errored', 'apply_patch 충돌 복구', 'Errored', 32],
    ['ses_foreign', 'Desktop에서 실행 중인 세션', 'ForeignBusy', 41],
    ['ses_aborted', '중단된 문서 작성 작업', 'Aborted', 55],
    ['ses_idle', '세션 정렬 규칙 점검', 'Idle', 70],
    ['ses_unknown', '상태 확인 불가 세션', 'Unknown', 90],
    ['ses_child', '하위 작업: MSW fixture 정리', 'Idle', 3, { parentId: 'ses_running' }],
  ]

  definitions.forEach(([sessionId, title, state, offset, extra]) => {
    const instance = ['ses_foreign', 'ses_aborted', 'ses_unknown'].includes(sessionId)
      ? claudeAlpha
      : openAlpha
    sessions[sessionId] = session(
      now,
      'mach_alpha',
      instance,
      sessionId,
      title,
      state,
      offset,
      extra,
    )
  })

  sessions.ses_gamma = session(
    now,
    'mach_gamma',
    'inst_gamma_opencode',
    'ses_gamma',
    '오프라인 머신의 캐시된 세션',
    'Idle',
    180,
  )

  const messages: Record<string, MessageView[]> = {}
  Object.values(sessions).forEach((value) => {
    const count = value.ref.sessionId === 'ses_running' ? 60 : 4
    messages[value.ref.sessionId] = messagesFor(
      now,
      value.ref.sessionId,
      count,
      value.title,
      value.displayState === 'Errored',
    )
  })

  const machines: MachineInfo[] = [
    {
      id: { value: 'mach_alpha' },
      hostname: 'MABI-DESKTOP',
      os: 'Windows 11 Pro 24H2',
      clientVersion: '1.5.0',
      online: true,
      lastSeen: new Date(now - 8_000).toISOString(),
      observedIp: '203.0.113.24',
      observedCountry: 'KR',
      observedIpSource: 'server',
      vpn: null,
      instances: [],
      workspaces: [
        {
          workspaceId: 'ws_mabi',
          name: 'Mabi Tooling',
          root: 'E:\\Programming\\Mabi',
          allowUpload: true,
          allowDownload: true,
          allowMkdir: true,
          allowDelete: false,
          denyPatterns: ['**/.env', '**/secrets/**'],
          linkedSessions: [],
        },
      ],
      health: {
        cpuPercent: 34,
        memoryUsedMb: 14_220,
        memoryTotalMb: 32_768,
        diskFreeMb: 412_000,
        uptime: '3d 7h',
        clockOffsetMs: 12,
        toolVersions: { dotnet: '10.0.100', node: '24.11.1', git: '2.51.0' },
        asOf: new Date(now).toISOString(),
      },
      keepAwake: true,
      features: [],
    },
    {
      id: { value: 'mach_beta' },
      hostname: 'HOME-LAPTOP',
      os: 'Windows 11 Home 24H2',
      clientVersion: '1.5.0',
      online: true,
      lastSeen: new Date(now - 21_000).toISOString(),
      observedIp: '198.51.100.77',
      observedCountry: 'JP',
      observedIpSource: 'server',
      vpn: null,
      instances: [],
      workspaces: [],
      health: {
        cpuPercent: 76,
        memoryUsedMb: 21_400,
        memoryTotalMb: 32_768,
        diskFreeMb: 88_000,
        uptime: '9h 12m',
        clockOffsetMs: -38,
        toolVersions: { dotnet: '10.0.100', node: '24.11.1', git: '2.51.0' },
        asOf: new Date(now).toISOString(),
      },
      keepAwake: false,
      features: [],
    },
    {
      id: { value: 'mach_gamma' },
      hostname: 'OFFICE-NUC',
      os: 'Windows 11 Pro 24H2',
      clientVersion: '1.4.2',
      online: false,
      lastSeen: new Date(now - 2 * hour).toISOString(),
      observedIp: null,
      observedCountry: null,
      observedIpSource: 'server',
      vpn: null,
      instances: [],
      workspaces: [],
      health: null,
      keepAwake: false,
      features: [],
    },
  ]

  const commands: Record<string, MockCommand> = {
    cmd_seed_prompt: seedCommand(
      now,
      'cmd_seed_prompt',
      'session.prompt',
      'Succeeded',
      8 * minute,
      'mach_alpha',
      { accepted: true, messageId: 'ses_idle_m4' },
    ),
    cmd_seed_refresh: seedCommand(
      now,
      'cmd_seed_refresh',
      'machine.instances.refresh',
      'Failed',
      24 * minute,
      'mach_alpha',
      null,
      {
        code: 'agent_no_ack',
        message: '에이전트가 확인 응답을 보내지 않았습니다.',
      },
    ),
    cmd_seed_abort: seedCommand(
      now,
      'cmd_seed_abort',
      'session.abort',
      'Succeeded',
      47 * minute,
      'mach_alpha',
      { aborted: true },
    ),
  }

  const audit: AuditLogView[] = [
    {
      id: 5,
      at: new Date(now - 8 * minute).toISOString(),
      actor: 'jdyun@example.com',
      ip: '203.0.113.24',
      country: 'KR',
      action: 'session.prompt',
      target: 'ses_idle',
      commandId: 'cmd_seed_prompt',
      result: 'succeeded',
    },
    {
      id: 4,
      at: new Date(now - 24 * minute).toISOString(),
      actor: 'jdyun@example.com',
      ip: '203.0.113.24',
      country: 'KR',
      action: 'machine.instances.refresh',
      target: 'mach_alpha',
      commandId: 'cmd_seed_refresh',
      result: 'failed',
    },
    {
      id: 3,
      at: new Date(now - 47 * minute).toISOString(),
      actor: 'jdyun@example.com',
      ip: '203.0.113.24',
      country: 'KR',
      action: 'session.abort',
      target: 'ses_aborted',
      commandId: 'cmd_seed_abort',
      result: 'succeeded',
    },
    {
      id: 2,
      at: new Date(now - 3 * hour).toISOString(),
      actor: 'system',
      ip: null,
      country: null,
      action: 'machine.enrolled',
      target: 'mach_beta',
      commandId: null,
      result: 'accepted',
    },
    {
      id: 1,
      at: new Date(now - 28 * hour).toISOString(),
      actor: 'jdyun@example.com',
      ip: '203.0.113.24',
      country: 'KR',
      action: 'auth.login',
      target: null,
      commandId: null,
      result: 'accepted',
    },
  ]

  const alphaLog = Array.from({ length: 180 }, (_, index) => {
    const at = new Date(now - (180 - index) * 30_000).toISOString()
    const line =
      index % 17 === 0
        ? 'warn: agent heartbeat delayed 812ms'
        : index % 11 === 0
          ? 'info: RemoteQueryService cache hit'
          : 'info: request completed 200'
    return `${at} [${index % 4 === 0 ? 'DBG' : 'INF'}] ${line} trace=${Math.random().toString(16).slice(2, 10)}`
  }).join('\n')

  return {
    now,
    machines,
    instances: {
      mach_alpha: [openCode, claudeCode],
      mach_beta: [betaOpenCode],
      mach_gamma: [
        {
          id: { value: 'inst_gamma_opencode' },
          kind: 'OpenCode',
          endpoint: 'http://127.0.0.1:4098',
          version: '1.17.4',
          mode: 'Managed',
          healthy: false,
          managed: true,
          capabilities: 'models,messages,sessions',
        },
      ],
    },
    features: {
      mach_alpha: [
        feature('sessions', 'Agents', 'Sessions', '/sessions'),
        feature('commands', 'Agents', 'Commands', '/commands'),
        feature('files', 'Workspace', '파일', '/files', false, '파일 API가 아직 없습니다.', '준비 중'),
        feature('vpn', 'Etc', 'ProtonVPN', '/vpn', false, 'VPN API가 아직 없습니다.', '준비 중'),
        feature('logs', 'Etc', '서버 로그', '/logs'),
        feature(
          'diagnostics',
          'Etc',
          '진단',
          '/diagnostics',
          false,
          '진단 API가 아직 없습니다.',
          '준비 중',
        ),
      ],
      mach_beta: [feature('sessions', 'Agents', 'Sessions', '/sessions')],
      mach_gamma: [feature('sessions', 'Agents', 'Sessions', '/sessions')],
    },
    sessions,
    messages,
    models: {
      inst_alpha_opencode: models,
      inst_alpha_claude: models,
      inst_beta_opencode: models,
      inst_gamma_opencode: models,
    },
    commands,
    audit,
    logs: {
      'server-2026-10-20.log': {
        file: {
          name: 'server-2026-10-20.log',
          sizeBytes: alphaLog.length,
          modifiedAt: new Date(now - 20_000).toISOString(),
        },
        content: alphaLog,
      },
      'server-2026-10-19.log': {
        file: {
          name: 'server-2026-10-19.log',
          sizeBytes: 46,
          modifiedAt: new Date(now - 24 * hour).toISOString(),
        },
        content: 'info: server started\ninfo: shutdown requested',
      },
    },
    enrollmentCodes: [],
  }
}

let mockState = createMockState()

export function getMockState(): MockState {
  return mockState
}

export function resetMockState(): MockState {
  mockState = createMockState()
  return mockState
}

function appendMessage(
  state: MockState,
  sessionId: string,
  role: 'user' | 'assistant',
  text: string,
): void {
  const list = state.messages[sessionId]
  if (!list) {
    return
  }
  const created = new Date().toISOString()
  list.push({
    messageId: `${sessionId}_live_${list.length}`,
    role,
    createdAt: created,
    completedAt: created,
    model: role === 'assistant' ? { providerId: 'anthropic', modelId: 'claude-sonnet-4-5' } : null,
    parts: [
      {
        partId: `${sessionId}_live_part_${list.length}`,
        type: 'text',
        text,
        tool: null,
      },
    ],
    error: null,
  })
}

export function advanceCommand(state: MockState, command: MockCommand): void {
  if (command.completed || !command.completionDelayMs || !command.outcome) {
    return
  }
  const now = Date.now()
  const elapsed = now - command.startedAt
  if (elapsed >= 180 && command.detail.steps.length < 2) {
    command.detail.steps.push(step(2, 'dispatched', now, 'Client로 명령을 전달했습니다.'))
    command.detail.status = 'Dispatched'
    command.detail.updatedAt = new Date(now).toISOString()
    return
  }
  if (elapsed >= 650 && command.detail.steps.length < 3) {
    command.detail.steps.push(step(3, 'running', now, '에이전트가 명령을 실행 중입니다.'))
    command.detail.status = 'Running'
    command.detail.updatedAt = new Date(now).toISOString()
    return
  }
  if (elapsed < command.completionDelayMs) {
    return
  }

  command.completed = true
  command.detail.status =
    command.outcome === 'succeeded'
      ? 'Succeeded'
      : command.outcome === 'failed'
        ? 'Failed'
        : 'Cancelled'
  command.detail.updatedAt = new Date(now).toISOString()
  command.detail.result =
    command.outcome === 'succeeded'
      ? { accepted: true, mode: command.mode }
      : null
  if (command.outcome === 'failed') {
    command.detail.errorCode = 'prompt_unconfirmed'
    command.detail.errorMessage = '프롬프트 수락을 확인하지 못했습니다.'
  }
  command.detail.steps.push(
    step(
      command.detail.steps.length + 1,
      command.outcome === 'succeeded' ? 'completed' : 'failed',
      now,
      command.outcome === 'succeeded' ? '명령이 완료되었습니다.' : '명령이 실패했습니다.',
    ),
  )

  if (command.sessionId) {
    const target = state.sessions[command.sessionId]
    if (target) {
      const idle = command.mode !== 'abort'
      target.displayState = idle ? 'Idle' : 'Aborted'
      target.runState = 'Idle'
      target.busySince = null
      target.updatedAt = new Date(now).toISOString()
      target.lastActivityAt = new Date(now).toISOString()
      if (idle && command.outcome === 'succeeded') {
        if (command.promptText) {
          appendMessage(state, command.sessionId, 'user', command.promptText)
        }
        appendMessage(
          state,
          command.sessionId,
          'assistant',
          '프롬프트를 받았습니다. 변경 사항과 테스트 결과를 정리하겠습니다.',
        )
        target.messageCount = (target.messageCount ?? 0) + 2
      }
    }
  }

  state.audit.unshift({
    id: state.audit.length + 1,
    at: new Date(now).toISOString(),
    actor: 'jdyun@example.com',
    ip: '203.0.113.24',
    country: 'KR',
    action: command.detail.type,
    target: command.sessionId ?? command.machineId,
    commandId: command.detail.commandId,
    result: command.outcome === 'succeeded' ? 'succeeded' : 'failed',
  })
}

export function liveMeta(state: MockState, machineId?: string): ApiMeta {
  const machine = machineId
    ? state.machines.find((item) => item.id.value === machineId)
    : undefined
  return meta(Date.now(), {
    machineOnline: machine?.online ?? true,
    source: machine && !machine.online ? 'cache' : 'live',
    stale: Boolean(machine && !machine.online),
  })
}

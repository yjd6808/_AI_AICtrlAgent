import { ApiError } from './errors'
import type {
  ApiEnvelope,
  AuditFilters,
  AuditLogView,
  BrowserUser,
  CommandDetailView,
  CommandFilters,
  CommandOutcome,
  CommandPendingView,
  EnrollmentCode,
  InstanceInfo,
  LogFileView,
  LogTextView,
  MachineFeature,
  MachineInfo,
  MessagePage,
  ModelOption,
  Problem,
  ProviderKind,
  SessionPage,
  SessionSummary,
  SessionView,
  PromptInput,
} from './types'

type QueryValue = string | number | boolean | null | undefined

export type RequestOptions = {
  method?: 'GET' | 'POST' | 'DELETE'
  query?: Record<string, QueryValue>
  body?: unknown
  signal?: AbortSignal
  requestId?: string
  idempotencyKey?: string
}

export type ApiResult<T> = {
  data: T
  status: number
  traceId: string | null
  requestId: string | null
}

const rawBase = (import.meta.env.VITE_API_BASE as string | undefined) ?? ''
const base = rawBase.replace(/\/+$/, '')

export const apiRoot = `${base}/api/v1`

function buildUrl(path: string, query?: Record<string, QueryValue>): string {
  const suffix = path.startsWith('/') ? path : `/${path}`
  const params = new URLSearchParams()
  if (query) {
    for (const [key, value] of Object.entries(query)) {
      if (value === undefined || value === null || value === '') {
        continue
      }
      params.set(key, String(value))
    }
  }
  const search = params.toString()
  return `${apiRoot}${suffix}${search ? `?${search}` : ''}`
}

function isProblem(value: unknown): value is Problem {
  return (
    typeof value === 'object' &&
    value !== null &&
    'code' in value &&
    'status' in value &&
    'title' in value
  )
}

async function parseBody(response: Response): Promise<unknown> {
  if (response.status === 204) {
    return undefined
  }
  const text = await response.text()
  if (!text) {
    return undefined
  }
  try {
    return JSON.parse(text) as unknown
  } catch {
    return text
  }
}

async function request<T>(path: string, options: RequestOptions = {}): Promise<ApiResult<T>> {
  const headers = new Headers({ Accept: 'application/json' })
  if (options.body !== undefined) {
    headers.set('Content-Type', 'application/json')
  }
  if (options.requestId) {
    headers.set('X-Request-Id', options.requestId)
  }
  if (options.idempotencyKey) {
    headers.set('Idempotency-Key', options.idempotencyKey)
  }

  const response = await fetch(buildUrl(path, options.query), {
    method: options.method ?? 'GET',
    credentials: 'same-origin',
    headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
    signal: options.signal,
  })

  const traceId = response.headers.get('X-Trace-Id')
  const requestId = response.headers.get('X-Request-Id')
  const parsed = await parseBody(response)

  if (!response.ok) {
    if (isProblem(parsed)) {
      throw ApiError.fromProblem(parsed, traceId)
    }
    const fallback = typeof parsed === 'string' ? parsed : null
    throw new ApiError(
      response.status,
      'invalid_response',
      fallback,
      traceId,
      response.statusText || 'Request failed',
    )
  }

  return { data: parsed as T, status: response.status, traceId, requestId }
}

function assertEnvelope<T>(value: unknown): ApiEnvelope<T> {
  if (
    typeof value !== 'object' ||
    value === null ||
    !('data' in value) ||
    !('meta' in value)
  ) {
    throw new ApiError(200, 'invalid_response', 'Expected an API envelope.', null)
  }
  return value as ApiEnvelope<T>
}

function machinePath(machineId: string): string {
  return `/machines/${encodeURIComponent(machineId)}`
}

function sessionPath(machineId: string, sessionId: string): string {
  return `${machinePath(machineId)}/sessions/${encodeURIComponent(sessionId)}`
}

export const api = {
  getEnvelope<T>(path: string, options: RequestOptions = {}): Promise<ApiResult<ApiEnvelope<T>>> {
    return request<unknown>(path, options).then((result) => ({
      ...result,
      data: assertEnvelope<T>(result.data),
    }))
  },

  getPlain<T>(path: string, options: RequestOptions = {}): Promise<ApiResult<T>> {
    return request<T>(path, options)
  },

  postPlain<T>(path: string, options: RequestOptions = {}): Promise<ApiResult<T>> {
    return request<T>(path, { ...options, method: 'POST' })
  },

  postCommand(path: string, options: RequestOptions): Promise<ApiResult<CommandOutcome>> {
    return request<unknown>(path, { ...options, method: 'POST' }).then((result) => {
      const envelope = assertEnvelope<CommandDetailView | CommandPendingView>(result.data)
      const data = envelope.data
      if (typeof data === 'object' && data !== null && 'commandId' in data) {
        return {
          ...result,
          data: { kind: 'detail' as const, id: data.commandId, command: data },
        }
      }
      if (typeof data === 'object' && data !== null && 'requestId' in data) {
        return {
          ...result,
          data: { kind: 'pending' as const, id: data.requestId, pending: data },
        }
      }
      throw new ApiError(200, 'invalid_response', 'Unknown command response shape.', result.traceId)
    })
  },

  delete(path: string, options: RequestOptions = {}): Promise<ApiResult<undefined>> {
    return request<undefined>(path, { ...options, method: 'DELETE' })
  },
}

export const apiPaths = {
  authMe: '/auth/me',
  machines: '/machines',
  machine: (machineId: string) => machinePath(machineId),
  instances: (machineId: string) => `${machinePath(machineId)}/instances`,
  features: (machineId: string) => `${machinePath(machineId)}/features`,
  sessions: (machineId: string) => `${machinePath(machineId)}/sessions`,
  session: sessionPath,
  messages: (machineId: string, sessionId: string) => `${sessionPath(machineId, sessionId)}/messages`,
  models: (machineId: string) => `${machinePath(machineId)}/models`,
  refreshInstances: (machineId: string) => `${machinePath(machineId)}/instances/refresh`,
  abort: (machineId: string, sessionId: string) => `${sessionPath(machineId, sessionId)}/abort`,
  prompt: (machineId: string, sessionId: string) => `${sessionPath(machineId, sessionId)}/prompt`,
  abortAndPrompt: (machineId: string, sessionId: string) =>
    `${sessionPath(machineId, sessionId)}/abort-and-prompt`,
  permission: (machineId: string, sessionId: string, requestId: string) =>
    `${sessionPath(machineId, sessionId)}/permissions/${encodeURIComponent(requestId)}`,
  commands: '/commands',
  command: (commandId: string) => `/commands/${encodeURIComponent(commandId)}`,
  audit: '/audit',
  serverLogs: '/logs/server',
  serverLog: (name: string) => `/logs/server/${encodeURIComponent(name)}`,
  enrollmentCodes: '/machines/enrollment-codes',
  revokeMachine: (machineId: string) => machinePath(machineId),
}

export function loginUrl(returnUrl: string): string {
  return `${apiRoot}/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`
}

export async function fetchMe(signal?: AbortSignal): Promise<BrowserUser> {
  const result = await api.getPlain<BrowserUser>(apiPaths.authMe, { signal })
  return result.data
}

export async function fetchMachines(signal?: AbortSignal) {
  const result = await api.getEnvelope<MachineInfo[]>(apiPaths.machines, { signal })
  return { data: result.data.data, meta: result.data.meta }
}

export async function fetchMachine(machineId: string, signal?: AbortSignal) {
  const result = await api.getEnvelope<MachineInfo>(apiPaths.machine(machineId), { signal })
  return { data: result.data.data, meta: result.data.meta }
}

export async function fetchInstances(machineId: string, kind?: ProviderKind, signal?: AbortSignal) {
  const result = await api.getEnvelope<InstanceInfo[]>(apiPaths.instances(machineId), {
    query: { kind },
    signal,
  })
  return { data: result.data.data, meta: result.data.meta }
}

export async function fetchFeatures(machineId: string, signal?: AbortSignal) {
  const result = await api.getEnvelope<MachineFeature[]>(apiPaths.features(machineId), { signal })
  return { data: result.data.data, meta: result.data.meta }
}

export async function fetchSessions(
  machineId: string,
  instanceId: string,
  view: SessionView,
  cursor: string | null | undefined,
  signal?: AbortSignal,
) {
  const result = await api.getEnvelope<SessionPage>(apiPaths.sessions(machineId), {
    query: { instance: instanceId, view, includeChildren: true, cursor },
    signal,
  })
  return { data: result.data.data, meta: result.data.meta }
}

export async function fetchSession(
  machineId: string,
  instanceId: string,
  sessionId: string,
  signal?: AbortSignal,
) {
  const result = await api.getEnvelope<SessionSummary>(apiPaths.session(machineId, sessionId), {
    query: { instance: instanceId },
    signal,
  })
  return { data: result.data.data, meta: result.data.meta }
}

export async function fetchMessages(
  machineId: string,
  instanceId: string,
  sessionId: string,
  cursor: string | null | undefined,
  limit = 50,
  signal?: AbortSignal,
) {
  const result = await api.getEnvelope<MessagePage>(
    apiPaths.messages(machineId, sessionId),
    {
      query: { instance: instanceId, limit, cursor },
      signal,
    },
  )
  return { data: result.data.data, meta: result.data.meta }
}

export async function fetchModels(
  machineId: string,
  instanceId: string | undefined,
  signal?: AbortSignal,
) {
  const result = await api.getEnvelope<ModelOption[]>(apiPaths.models(machineId), {
    query: { instance: instanceId },
    signal,
  })
  return { data: result.data.data, meta: result.data.meta }
}

export async function fetchCommands(filters: CommandFilters, signal?: AbortSignal) {
  const result = await api.getEnvelope<CommandDetailView[]>(apiPaths.commands, {
    query: {
      machineId: filters.machineId,
      status: filters.status,
      limit: filters.limit ?? 200,
    },
    signal,
  })
  return { data: result.data.data, meta: result.data.meta }
}

export async function fetchCommand(commandId: string, signal?: AbortSignal) {
  const result = await api.getEnvelope<CommandDetailView>(apiPaths.command(commandId), { signal })
  return { data: result.data.data, meta: result.data.meta }
}

export async function fetchAudit(filters: AuditFilters, signal?: AbortSignal) {
  const result = await api.getEnvelope<AuditLogView[]>(apiPaths.audit, {
    query: { from: filters.from, to: filters.to, limit: filters.limit ?? 100 },
    signal,
  })
  return { data: result.data.data, meta: result.data.meta }
}

export async function fetchServerLogs(signal?: AbortSignal) {
  const result = await api.getEnvelope<LogFileView[]>(apiPaths.serverLogs, { signal })
  return { data: result.data.data, meta: result.data.meta }
}

export async function fetchServerLog(name: string, lines = 500, signal?: AbortSignal) {
  const result = await api.getEnvelope<LogTextView>(apiPaths.serverLog(name), {
    query: { lines },
    signal,
  })
  return { data: result.data.data, meta: result.data.meta }
}

export async function createEnrollmentCode(): Promise<EnrollmentCode> {
  const result = await api.postPlain<EnrollmentCode>(apiPaths.enrollmentCodes)
  return result.data
}

export async function revokeMachine(machineId: string): Promise<void> {
  await api.delete(apiPaths.revokeMachine(machineId))
}

export async function issuePrompt(
  machineId: string,
  instanceId: string,
  sessionId: string,
  input: PromptInput,
  requestId: string,
) {
  const result = await api.postCommand(apiPaths.prompt(machineId, sessionId), {
    query: { instance: instanceId, wait: 0 },
    body: {
      text: input.text,
      model: input.model ?? undefined,
      agent: input.agent ?? undefined,
    },
    requestId,
    idempotencyKey: requestId,
  })
  return result
}

export async function issueAbortAndPrompt(
  machineId: string,
  instanceId: string,
  sessionId: string,
  input: PromptInput,
  requestId: string,
) {
  const result = await api.postCommand(apiPaths.abortAndPrompt(machineId, sessionId), {
    query: { instance: instanceId, wait: 0 },
    body: { text: input.text, model: input.model ?? undefined },
    requestId,
    idempotencyKey: requestId,
  })
  return result
}

export async function issueAbort(
  machineId: string,
  instanceId: string,
  sessionId: string,
  requestId: string,
) {
  const result = await api.postCommand(apiPaths.abort(machineId, sessionId), {
    query: { instance: instanceId, wait: 0 },
    requestId,
    idempotencyKey: requestId,
  })
  return result
}

export async function replyPermission(
  machineId: string,
  instanceId: string,
  sessionId: string,
  requestId: string,
  response: 'once' | 'always' | 'reject',
) {
  const writeId = requestId
  const result = await api.postCommand(
    apiPaths.permission(machineId, sessionId, requestId),
    {
      query: { instance: instanceId, wait: 0 },
      body: { response },
      requestId: writeId,
      idempotencyKey: writeId,
    },
  )
  return result
}

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
  FileListPage,
  FilePreviewView,
  FileSort,
  FileStatView,
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
  TransferCommandView,
  TransferCreatedView,
  TransferStatusView,
  UploadConflict,
  VpnConnectInput,
  VpnPageSnapshot,
  WorkspaceInfo,
} from './types'

type QueryValue = string | number | boolean | null | undefined

export type RequestOptions = {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE'
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

function isBinaryBody(value: unknown): value is Blob | ArrayBuffer | ArrayBufferView {
  return (
    value instanceof Blob ||
    value instanceof ArrayBuffer ||
    ArrayBuffer.isView(value)
  )
}

async function request<T>(path: string, options: RequestOptions = {}): Promise<ApiResult<T>> {
  const headers = new Headers({ Accept: 'application/json' })
  let payload: BodyInit | undefined
  if (options.body !== undefined) {
    if (isBinaryBody(options.body)) {
      payload = options.body as BodyInit
      const type = options.body instanceof Blob ? options.body.type : ''
      headers.set('Content-Type', type || 'application/octet-stream')
    } else {
      headers.set('Content-Type', 'application/json')
      payload = JSON.stringify(options.body)
    }
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
    body: payload,
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

  postEnvelope<T>(path: string, options: RequestOptions = {}): Promise<ApiResult<ApiEnvelope<T>>> {
    return request<unknown>(path, { ...options, method: 'POST' }).then((result) => ({
      ...result,
      data: assertEnvelope<T>(result.data),
    }))
  },

  putEnvelope<T>(path: string, options: RequestOptions = {}): Promise<ApiResult<ApiEnvelope<T>>> {
    return request<unknown>(path, { ...options, method: 'PUT' }).then((result) => ({
      ...result,
      data: assertEnvelope<T>(result.data),
    }))
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
  workspaces: (machineId: string) => `${machinePath(machineId)}/workspaces`,
  files: (machineId: string, workspaceId: string) =>
    `${machinePath(machineId)}/workspaces/${encodeURIComponent(workspaceId)}/files`,
  fileStat: (machineId: string, workspaceId: string) =>
    `${machinePath(machineId)}/workspaces/${encodeURIComponent(workspaceId)}/files/stat`,
  filePreview: (machineId: string, workspaceId: string) =>
    `${machinePath(machineId)}/workspaces/${encodeURIComponent(workspaceId)}/files/preview`,
  fileMkdir: (machineId: string, workspaceId: string) =>
    `${machinePath(machineId)}/workspaces/${encodeURIComponent(workspaceId)}/files/mkdir`,
  fileDelete: (machineId: string, workspaceId: string) =>
    `${machinePath(machineId)}/workspaces/${encodeURIComponent(workspaceId)}/files/delete`,
  fileRename: (machineId: string, workspaceId: string) =>
    `${machinePath(machineId)}/workspaces/${encodeURIComponent(workspaceId)}/files/rename`,
  uploads: (machineId: string, workspaceId: string) =>
    `${machinePath(machineId)}/workspaces/${encodeURIComponent(workspaceId)}/transfers/uploads`,
  downloads: (machineId: string, workspaceId: string) =>
    `${machinePath(machineId)}/workspaces/${encodeURIComponent(workspaceId)}/transfers/downloads`,
  transfer: (machineId: string, transferId: string) =>
    `${machinePath(machineId)}/transfers/${encodeURIComponent(transferId)}`,
  transferChunk: (machineId: string, transferId: string, index: number) =>
    `${machinePath(machineId)}/transfers/${encodeURIComponent(transferId)}/chunks/${index}`,
  transferComplete: (machineId: string, transferId: string) =>
    `${machinePath(machineId)}/transfers/${encodeURIComponent(transferId)}/complete`,
  transferDispatch: (machineId: string, transferId: string) =>
    `${machinePath(machineId)}/transfers/${encodeURIComponent(transferId)}/dispatch`,
  transferContent: (machineId: string, transferId: string) =>
    `${machinePath(machineId)}/transfers/${encodeURIComponent(transferId)}/content`,
  vpnPage: (machineId: string) => `${machinePath(machineId)}/vpn/page`,
  vpnConnect: (machineId: string) => `${machinePath(machineId)}/vpn/connect`,
  vpnDisconnect: (machineId: string) => `${machinePath(machineId)}/vpn/disconnect`,
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

export async function fetchWorkspaces(machineId: string, signal?: AbortSignal) {
  const result = await api.getEnvelope<WorkspaceInfo[]>(apiPaths.workspaces(machineId), {
    signal,
  })
  return { data: result.data.data, meta: result.data.meta }
}

export async function fetchFiles(
  machineId: string,
  workspaceId: string,
  path: string,
  sort: FileSort,
  cursor: string | null | undefined,
  signal?: AbortSignal,
) {
  const result = await api.getEnvelope<FileListPage>(apiPaths.files(machineId, workspaceId), {
    query: { path, sort, cursor, limit: 500 },
    signal,
  })
  return { data: result.data.data, meta: result.data.meta }
}

export async function fetchFileStat(
  machineId: string,
  workspaceId: string,
  path: string,
  signal?: AbortSignal,
) {
  const result = await api.getEnvelope<FileStatView>(apiPaths.fileStat(machineId, workspaceId), {
    query: { path },
    signal,
  })
  return { data: result.data.data, meta: result.data.meta }
}

export async function fetchFilePreview(
  machineId: string,
  workspaceId: string,
  path: string,
  signal?: AbortSignal,
) {
  const result = await api.getEnvelope<FilePreviewView>(
    apiPaths.filePreview(machineId, workspaceId),
    { query: { path }, signal },
  )
  return { data: result.data.data, meta: result.data.meta }
}

export async function mkdir(
  machineId: string,
  workspaceId: string,
  relPath: string,
  requestId: string,
) {
  return api.postCommand(apiPaths.fileMkdir(machineId, workspaceId), {
    body: { relPath },
    requestId,
    idempotencyKey: requestId,
  })
}

export async function deleteFile(
  machineId: string,
  workspaceId: string,
  relPath: string,
  requestId: string,
) {
  return api.postCommand(apiPaths.fileDelete(machineId, workspaceId), {
    body: { relPath },
    requestId,
    idempotencyKey: requestId,
  })
}

export async function renameFile(
  machineId: string,
  workspaceId: string,
  relPath: string,
  newName: string,
  requestId: string,
) {
  return api.postCommand(apiPaths.fileRename(machineId, workspaceId), {
    body: { relPath, newName },
    requestId,
    idempotencyKey: requestId,
  })
}

export async function createUpload(
  machineId: string,
  workspaceId: string,
  input: { relDir: string; fileName: string; size: number; sha256?: string; conflict: UploadConflict },
) {
  const result = await api.postEnvelope<TransferCreatedView>(
    apiPaths.uploads(machineId, workspaceId),
    {
      body: {
        relDir: input.relDir,
        fileName: input.fileName,
        size: input.size,
        sha256: input.sha256 ?? null,
        conflict: input.conflict,
      },
    },
  )
  return result.data.data
}

export async function putTransferChunk(
  machineId: string,
  transferId: string,
  index: number,
  chunk: Blob,
) {
  const result = await api.putEnvelope<TransferStatusView>(
    apiPaths.transferChunk(machineId, transferId, index),
    { body: chunk },
  )
  return result.data.data
}

export async function completeTransfer(machineId: string, transferId: string) {
  const result = await api.postEnvelope<TransferStatusView>(
    apiPaths.transferComplete(machineId, transferId),
  )
  return result.data.data
}

export async function dispatchTransfer(machineId: string, transferId: string) {
  const result = await api.postEnvelope<TransferCommandView>(
    apiPaths.transferDispatch(machineId, transferId),
  )
  return result.data.data
}

export async function fetchTransfer(
  machineId: string,
  transferId: string,
  signal?: AbortSignal,
) {
  const result = await api.getEnvelope<TransferStatusView>(
    apiPaths.transfer(machineId, transferId),
    { signal },
  )
  return result.data.data
}

export async function createDownload(
  machineId: string,
  workspaceId: string,
  relPath: string,
  asZip: boolean,
) {
  const result = await api.postEnvelope<TransferCreatedView>(
    apiPaths.downloads(machineId, workspaceId),
    { body: { relPath, asZip } },
  )
  return result.data.data
}

export function transferContentUrl(machineId: string, transferId: string) {
  return buildUrl(apiPaths.transferContent(machineId, transferId))
}

export async function fetchVpnPage(
  machineId: string,
  country: string | undefined,
  prevCity: string | undefined,
  signal?: AbortSignal,
) {
  const result = await api.getEnvelope<VpnPageSnapshot>(apiPaths.vpnPage(machineId), {
    query: { country, prevCity },
    signal,
  })
  return { data: result.data.data, meta: result.data.meta }
}

export async function connectVpn(
  machineId: string,
  input: VpnConnectInput,
  requestId: string,
) {
  return api.postCommand(apiPaths.vpnConnect(machineId), {
    query: { wait: 0 },
    body: {
      server: input.server ?? null,
      country: input.country ?? null,
      city: input.city ?? null,
      force: input.force ?? false,
    },
    requestId,
    idempotencyKey: requestId,
  })
}

export async function disconnectVpn(machineId: string, requestId: string) {
  return api.postCommand(apiPaths.vpnDisconnect(machineId), {
    query: { wait: 0 },
    requestId,
    idempotencyKey: requestId,
  })
}

export type Iso = string

export type Id = { value: string }

export type SessionRef = { machine: Id; instance: Id; sessionId: string }

export type ApiMeta = {
  source: 'live' | 'cache'
  asOf: Iso
  machineOnline: boolean
  stale: boolean
  latencyMs: number
  traceId: string
}

export type ApiEnvelope<T> = { data: T; meta: ApiMeta }

export type Problem = {
  type: string
  title: string
  status: number
  detail: string | null
  instance: string | null
  code: string
}

export type ProviderKind = 'OpenCode' | 'ClaudeCode'

export type InstanceMode = 'Managed' | 'SharedStore'

export type AcRunState =
  | 'Unknown'
  | 'Idle'
  | 'Busy'
  | 'Retrying'
  | 'WaitingPermission'
  | 'Errored'
  | 'ForeignBusy'

export type ErrorClass =
  | 'None'
  | 'QuotaExceeded'
  | 'RateLimited'
  | 'AuthFailed'
  | 'ModelUnavailable'
  | 'Network'
  | 'Aborted'
  | 'ToolError'
  | 'Unknown'

export type SessionDisplayState =
  | 'Running'
  | 'Retrying'
  | 'WaitingPermission'
  | 'Stalled'
  | 'QuotaBlocked'
  | 'Errored'
  | 'ForeignBusy'
  | 'Aborted'
  | 'Idle'
  | 'Unknown'

export type FeatureSection = 'Agents' | 'Workspace' | 'Etc'

export type CommandStatus =
  | 'Created'
  | 'Queued'
  | 'Dispatched'
  | 'Acknowledged'
  | 'Running'
  | 'Succeeded'
  | 'Failed'
  | 'Cancelled'
  | 'Expired'
  | 'Unknown'

export type ModelRef = { providerId: string; modelId: string }

export type RetryInfo = { attempt: number; message: string | null; nextAt: Iso | null }

export type ModelOption = { providerId: string; modelId: string; name: string | null }

export type MachineFeature = {
  key: string
  section: FeatureSection
  title: string
  route: string
  available: boolean
  unavailableReason: string | null
  badge: string | null
}

export type InstanceInfo = {
  id: Id
  kind: ProviderKind
  endpoint: string
  version: string | null
  mode: InstanceMode
  healthy: boolean
  managed: boolean
  capabilities: string
}

export type WorkspaceInfo = {
  workspaceId: string
  name: string
  root: string
  allowUpload: boolean
  allowDownload: boolean
  allowMkdir: boolean
  allowDelete: boolean
  denyPatterns: string[]
  linkedSessions: SessionRef[]
}

export type MachineHealth = {
  cpuPercent: number | null
  memoryUsedMb: number | null
  memoryTotalMb: number | null
  diskFreeMb: number | null
  uptime: string | null
  clockOffsetMs: number | null
  toolVersions: Record<string, string | null>
  asOf: Iso
}

export type MachineInfo = {
  id: Id
  hostname: string
  os: string
  clientVersion: string
  online: boolean
  lastSeen: Iso
  observedIp: string | null
  observedCountry: string | null
  observedIpSource: string
  vpn: unknown | null
  instances: InstanceInfo[]
  workspaces: WorkspaceInfo[]
  health: MachineHealth | null
  keepAwake: boolean
  features: MachineFeature[]
}

export type SessionSummary = {
  ref: SessionRef
  title: string
  parentId: string | null
  directory: string
  runState: AcRunState
  lastError: ErrorClass
  lastErrorMessage: string | null
  updatedAt: Iso
  lastModel: ModelRef | null
  retry: RetryInfo | null
  displayState: SessionDisplayState
  busySince: Iso | null
  lastActivityAt: Iso | null
  messageCount: number | null
  activeOnInstance: boolean
}

export type SessionPage = {
  sessions: SessionSummary[]
  nextCursor: string | null
  hasMore: boolean
}

export type MessagePage = {
  messages: MessageView[]
  nextCursor: string | null
  hasMore: boolean
}

export type PartView = {
  partId: string
  type: string
  text: string | null
  tool: ToolPartView | null
}

export type ToolPartView = {
  tool: string
  status: string
  title: string | null
  outputPreview: string | null
  input: string | null
  startedAt: Iso | null
  completedAt: Iso | null
}

export type ErrorInfo = {
  class: ErrorClass
  name: string
  message: string
  statusCode: number | null
  rawJson: string | null
}

export type MessageView = {
  messageId: string
  role: string
  createdAt: Iso
  completedAt: Iso | null
  model: ModelRef | null
  parts: PartView[]
  error: ErrorInfo | null
}

export type CommandStepView = { seq: number; step: string; message: string | null; at: Iso }

export type CommandDetailView = {
  commandId: string
  type: string
  status: CommandStatus
  result: unknown
  errorCode: string | null
  errorMessage: string | null
  issuedBy: string
  issuedAt: Iso
  updatedAt: Iso
  steps: CommandStepView[]
}

export type CommandPendingView = {
  requestId: string
  status: string
  lastStep: string | null
}

export type AuditLogView = {
  id: number
  at: Iso
  actor: string
  ip: string | null
  country: string | null
  action: string
  target: string | null
  commandId: string | null
  result: string
}

export type LogFileView = { name: string; sizeBytes: number; modifiedAt: Iso }

export type LogTextView = { name: string; content: string; truncated: boolean }

export type BrowserUser = { email: string; name: string | null; authTime: string | null }

export type EnrollmentCode = { code: string; expiresAt: Iso }

export type CommandOutcome =
  | { kind: 'detail'; id: string; command: CommandDetailView }
  | { kind: 'pending'; id: string; pending: CommandPendingView }

export type PromptInput = {
  text: string
  model?: ModelRef | null
  agent?: string | null
}

export type SessionView = 'active' | 'all'

export type AuditFilters = { from?: string; to?: string; limit?: number }

export type CommandFilters = { machineId?: string; status?: CommandStatus; limit?: number }

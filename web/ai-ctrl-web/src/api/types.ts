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

export type FileSort = 'name' | 'modified' | 'size'

export type FileEntry = {
  name: string
  relPath: string
  isDir: boolean
  size: number | null
  modified: Iso
  hidden: boolean
  denied: boolean
  isReparsePoint: boolean
}

export type FileListPage = {
  entries: FileEntry[]
  nextCursor: string | null
  hasMore: boolean
}

export type FileStatView = {
  name: string
  relPath: string
  isDir: boolean
  size: number | null
  modified: Iso
  hidden: boolean
  denied: boolean
  isReparsePoint: boolean
  sha256: string | null
}

export type FileMutationResult = {
  workspaceId: string
  relPath: string
  isDir: boolean
}

export type FilePreviewKind = 'text' | 'image' | 'binary'

export type FilePreviewView = {
  name: string
  relPath: string
  kind: FilePreviewKind
  size: number
  modified: Iso
  text: string | null
  base64: string | null
  truncated: boolean
}

export type TransferDirection = 'up' | 'down'

export type TransferStatusName =
  | 'pending'
  | 'uploading'
  | 'ready'
  | 'downloading'
  | 'completed'
  | 'failed'
  | 'expired'

export type UploadConflict = 'fail' | 'overwrite' | 'rename'

export type TransferCreatedView = {
  transferId: string
  chunkSize: number
  expiresAt: Iso
}

export type TransferStatusView = {
  transferId: string
  direction: TransferDirection
  machineId: string
  workspaceId: string
  relPath: string
  size: number
  sha256: string | null
  asZip: boolean
  conflict: UploadConflict | null
  chunkSize: number
  chunksTotal: number
  receivedChunks: number[]
  status: TransferStatusName
  createdAt: Iso
  expiresAt: Iso
  error: string | null
}

export type TransferCommandView = {
  transferId: string
  commandId: string
  status: string
  pending: boolean
}

export type VpnRowState = 'Connected' | 'Recent' | 'Past' | 'Never'

export type VpnStatusView = {
  cliFound: boolean
  cliPath: string | null
  helperAvailable: boolean
  protonGuiRunning: boolean
  signedIn: boolean
  username: string | null
  expiresAt: Iso | null
  tunnelUp: boolean
  server: string | null
  country: string | null
  city: string | null
  detail: string | null
  exitIp: string | null
  statusText: string
  busyOperation: string | null
  busyCommandId: string | null
  asOf: Iso
  usesProtonApp?: boolean
}

export type VpnCountryView = {
  code: string
  name: string
  count: number
  cities: string[]
}

export type VpnServerRow = {
  name: string
  country: string
  countryName: string
  city: string | null
  tier: number
  tierName: string
  load: number
  score: number
  features: string[]
  lastConnectedAt: Iso | null
  rowState: VpnRowState
}

export type VpnCityGroup = {
  city: string
  count: number
  highlight: boolean
  servers: VpnServerRow[]
}

export type VpnServerPage = {
  country: string
  cities: VpnCityGroup[]
  suggestedCity: string | null
  asOf: Iso
}

export type VpnPageSnapshot = {
  status: VpnStatusView
  countries: VpnCountryView[]
  selectedCountry: string
  servers: VpnServerPage | null
  actionText: string | null
}

export type VpnConnectInput = {
  server?: string
  country?: string
  city?: string
  force?: boolean
}

export type VpnServerStatus =
  | 'ok'
  | 'quota_exhausted'
  | 'opencode_timeout'
  | 'connect_failed'

export type VpnServerPolicyStateView = {
  server: string
  status: VpnServerStatus
  country: string | null
  city: string | null
  lastUsedAt: Iso | null
  availableAt: Iso | null
  lastExitIp: string | null
  failureCount: number
  lastError: string | null
}

export type VpnPolicyView = {
  enabled: boolean
  minLoad: number
  maxLoad: number
  excludeUsedWithinHours: number
  maxAttemptsPerSession: number
  retryWaitThresholdSeconds: number
  countries: string[]
  currentServer: string | null
  updatedAt: Iso
  servers: VpnServerPolicyStateView[]
}

export type VpnPolicyUpdateInput = {
  enabled: boolean
  minLoad: number
  maxLoad: number
  excludeUsedWithinHours: number
  maxAttemptsPerSession: number
  retryWaitThresholdSeconds: number
  countries: string[]
}

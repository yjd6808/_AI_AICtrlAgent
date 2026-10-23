import { useEffect, useRef, useState } from 'react'
import {
  useInfiniteQuery,
  useMutation,
  useQuery,
  useQueryClient,
} from '@tanstack/react-query'
import {
  completeTransfer,
  connectVpn,
  createDownload,
  createEnrollmentCode,
  createUpload,
  deleteFile,
  disconnectVpn,
  fetchAudit,
  fetchCommand,
  fetchCommands,
  fetchFeatures,
  fetchFilePreview,
  fetchFiles,
  fetchInstances,
  fetchMachine,
  fetchMachines,
  fetchMe,
  fetchMessages,
  fetchModels,
  fetchServerLog,
  fetchServerLogs,
  fetchSession,
  fetchSessions,
  fetchTransfer,
  fetchVpnPage,
  fetchWorkspaces,
  issueAbort,
  issueAbortAndPrompt,
  issuePrompt,
  mkdir,
  putTransferChunk,
  renameFile,
  replyPermission,
  revokeMachine,
  transferContentUrl,
  apiPaths,
  api,
} from './client'
import type {
  AuditFilters,
  CommandFilters,
  CommandStatus,
  FileSort,
  PromptInput,
  ProviderKind,
  SessionView,
  UploadConflict,
  VpnConnectInput,
} from './types'
import { ulid } from 'ulid'

export const queryKeys = {
  me: ['me'] as const,
  machines: ['machines'] as const,
  machine: (machineId: string) => ['machine', machineId] as const,
  instances: (machineId: string) => ['instances', machineId] as const,
  features: (machineId: string) => ['features', machineId] as const,
  sessions: (machineId: string, instanceId: string, view: SessionView) =>
    ['sessions', machineId, instanceId, view] as const,
  session: (machineId: string, instanceId: string, sessionId: string) =>
    ['session', machineId, instanceId, sessionId] as const,
  messages: (
    machineId: string,
    instanceId: string,
    sessionId: string,
    limit = 50,
  ) => ['messages', machineId, instanceId, sessionId, limit] as const,
  models: (machineId: string, instanceId: string | undefined) =>
    ['models', machineId, instanceId ?? 'all'] as const,
  commands: (filters: CommandFilters) => ['commands', filters] as const,
  command: (commandId: string) => ['command', commandId] as const,
  audit: (filters: AuditFilters) => ['audit', filters] as const,
  serverLogs: ['server-logs'] as const,
  serverLog: (name: string, lines: number) => ['server-log', name, lines] as const,
  workspaces: (machineId: string) => ['workspaces', machineId] as const,
  files: (machineId: string, workspaceId: string, path: string, sort: FileSort) =>
    ['files', machineId, workspaceId, path, sort] as const,
  filePreview: (machineId: string, workspaceId: string, path: string) =>
    ['file-preview', machineId, workspaceId, path] as const,
  vpnPage: (machineId: string, country: string, prevCity: string) =>
    ['vpn-page', machineId, country, prevCity] as const,
}

const terminalCommandStatuses: CommandStatus[] = [
  'Succeeded',
  'Failed',
  'Cancelled',
  'Expired',
  'Unknown',
]

export function isTerminalCommandStatus(status: CommandStatus | undefined): boolean {
  return status !== undefined && terminalCommandStatuses.includes(status)
}

export function useMe() {
  return useQuery({
    queryKey: queryKeys.me,
    queryFn: ({ signal }) => fetchMe(signal),
    retry: false,
    staleTime: 5 * 60_000,
    refetchOnWindowFocus: true,
  })
}

export function useMachines() {
  return useQuery({
    queryKey: queryKeys.machines,
    queryFn: ({ signal }) => fetchMachines(signal),
    refetchInterval: 10_000,
    refetchIntervalInBackground: false,
  })
}

export function useMachine(machineId: string | undefined) {
  return useQuery({
    queryKey: queryKeys.machine(machineId ?? ''),
    queryFn: ({ signal }) => fetchMachine(machineId ?? '', signal),
    enabled: Boolean(machineId),
    refetchInterval: 10_000,
    refetchIntervalInBackground: false,
  })
}

export function useInstances(machineId: string | undefined, kind?: ProviderKind) {
  return useQuery({
    queryKey: queryKeys.instances(machineId ?? ''),
    queryFn: ({ signal }) => fetchInstances(machineId ?? '', kind, signal),
    enabled: Boolean(machineId),
    refetchInterval: 15_000,
    refetchIntervalInBackground: false,
  })
}

export function useFeatures(machineId: string | undefined) {
  return useQuery({
    queryKey: queryKeys.features(machineId ?? ''),
    queryFn: ({ signal }) => fetchFeatures(machineId ?? '', signal),
    enabled: Boolean(machineId),
    refetchInterval: 15_000,
    refetchIntervalInBackground: false,
  })
}

export function useSessions(
  machineId: string | undefined,
  instanceId: string | undefined,
  view: SessionView,
) {
  return useQuery({
    queryKey: queryKeys.sessions(machineId ?? '', instanceId ?? '', view),
    queryFn: ({ signal }) => fetchSessions(machineId ?? '', instanceId ?? '', view, null, signal),
    enabled: Boolean(machineId && instanceId),
    refetchInterval: 5_000,
    refetchIntervalInBackground: false,
  })
}

export function useSession(
  machineId: string | undefined,
  instanceId: string | undefined,
  sessionId: string | undefined,
) {
  return useQuery({
    queryKey: queryKeys.session(machineId ?? '', instanceId ?? '', sessionId ?? ''),
    queryFn: ({ signal }) =>
      fetchSession(machineId ?? '', instanceId ?? '', sessionId ?? '', signal),
    enabled: Boolean(machineId && instanceId && sessionId),
    refetchInterval: 5_000,
    refetchIntervalInBackground: false,
  })
}

export function useMessages(
  machineId: string | undefined,
  instanceId: string | undefined,
  sessionId: string | undefined,
  limit = 50,
) {
  return useInfiniteQuery({
    queryKey: queryKeys.messages(
      machineId ?? '',
      instanceId ?? '',
      sessionId ?? '',
      limit,
    ),
    queryFn: ({ pageParam, signal }) =>
      fetchMessages(
        machineId ?? '',
        instanceId ?? '',
        sessionId ?? '',
        pageParam,
        limit,
        signal,
      ),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (lastPage) => lastPage.data.nextCursor ?? undefined,
    enabled: Boolean(machineId && instanceId && sessionId),
    refetchInterval: 2_000,
    refetchIntervalInBackground: false,
  })
}

export function useModels(machineId: string | undefined, instanceId: string | undefined) {
  return useQuery({
    queryKey: queryKeys.models(machineId ?? '', instanceId),
    queryFn: ({ signal }) => fetchModels(machineId ?? '', instanceId, signal),
    enabled: Boolean(machineId),
    staleTime: 60_000,
  })
}

export function useCommands(filters: CommandFilters, enabled = true) {
  return useQuery({
    queryKey: queryKeys.commands(filters),
    queryFn: ({ signal }) => fetchCommands(filters, signal),
    enabled,
    refetchInterval: (query) => {
      const data = query.state.data
      const hasPending = data?.data.some((command) => !isTerminalCommandStatus(command.status))
      return hasPending ? 1_000 : 5_000
    },
    refetchIntervalInBackground: false,
  })
}

export function useCommandDetail(commandId: string | undefined, enabled = true) {
  return useQuery({
    queryKey: queryKeys.command(commandId ?? ''),
    queryFn: ({ signal }) => fetchCommand(commandId ?? '', signal),
    enabled: Boolean(commandId) && enabled,
    refetchInterval: (query) => {
      if (!commandId) {
        return false
      }
      const status = query.state.data?.data.status
      return isTerminalCommandStatus(status) ? false : 1_000
    },
    refetchIntervalInBackground: false,
  })
}

export function useCommandFollow(commandId: string | null) {
  const [expired, setExpired] = useState(false)
  const trackedId = useRef<string | null>(null)

  useEffect(() => {
    if (trackedId.current === commandId) {
      return
    }
    trackedId.current = commandId
    setExpired(false)
    if (!commandId) {
      return
    }
    const timer = window.setTimeout(() => setExpired(true), 60_000)
    return () => window.clearTimeout(timer)
  }, [commandId])

  const query = useQuery({
    queryKey: queryKeys.command(commandId ?? ''),
    queryFn: ({ signal }) => fetchCommand(commandId ?? '', signal),
    enabled: Boolean(commandId),
    refetchInterval: (current) => {
      if (!commandId || expired) {
        return false
      }
      return isTerminalCommandStatus(current.state.data?.data.status) ? false : 1_000
    },
    refetchIntervalInBackground: false,
  })

  const status = query.data?.data.status

  return {
    command: query.data?.data,
    meta: query.data?.meta,
    error: query.error,
    isLoading: query.isLoading,
    expired: expired && !isTerminalCommandStatus(status),
    status,
  }
}

export function useAudit(filters: AuditFilters, enabled = true, pollMs = 15_000) {
  return useQuery({
    queryKey: queryKeys.audit(filters),
    queryFn: ({ signal }) => fetchAudit(filters, signal),
    enabled,
    refetchInterval: pollMs,
    refetchIntervalInBackground: false,
  })
}

export function useServerLogs() {
  return useQuery({
    queryKey: queryKeys.serverLogs,
    queryFn: ({ signal }) => fetchServerLogs(signal),
    refetchInterval: 15_000,
    refetchIntervalInBackground: false,
  })
}

export function useServerLog(name: string | undefined, lines = 500) {
  return useQuery({
    queryKey: queryKeys.serverLog(name ?? '', lines),
    queryFn: ({ signal }) => fetchServerLog(name ?? '', lines, signal),
    enabled: Boolean(name),
    refetchInterval: 5_000,
    refetchIntervalInBackground: false,
  })
}

export function useWorkspaces(machineId: string | undefined) {
  return useQuery({
    queryKey: queryKeys.workspaces(machineId ?? ''),
    queryFn: ({ signal }) => fetchWorkspaces(machineId ?? '', signal),
    enabled: Boolean(machineId),
    refetchInterval: 20_000,
    refetchIntervalInBackground: false,
  })
}

export function useFiles(
  machineId: string | undefined,
  workspaceId: string | undefined,
  path: string,
  sort: FileSort,
) {
  return useInfiniteQuery({
    queryKey: queryKeys.files(machineId ?? '', workspaceId ?? '', path, sort),
    queryFn: ({ pageParam, signal }) =>
      fetchFiles(machineId ?? '', workspaceId ?? '', path, sort, pageParam, signal),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (lastPage) => lastPage.data.nextCursor ?? undefined,
    enabled: Boolean(machineId && workspaceId),
    refetchInterval: 20_000,
    refetchIntervalInBackground: false,
  })
}

export function useFilePreview(
  machineId: string | undefined,
  workspaceId: string | undefined,
  path: string | undefined,
) {
  return useQuery({
    queryKey: queryKeys.filePreview(machineId ?? '', workspaceId ?? '', path ?? ''),
    queryFn: ({ signal }) =>
      fetchFilePreview(machineId ?? '', workspaceId ?? '', path ?? '', signal),
    enabled: Boolean(machineId && workspaceId && path),
    staleTime: 15_000,
  })
}

function refreshFiles(
  queryClient: ReturnType<typeof useQueryClient>,
  machineId: string,
  workspaceId: string,
) {
  const key = ['files', machineId, workspaceId]
  void queryClient.invalidateQueries({ queryKey: key })
  window.setTimeout(() => {
    void queryClient.invalidateQueries({ queryKey: key })
  }, 1_500)
}

export function useMkdir(machineId: string, workspaceId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ relPath }: { relPath: string }) =>
      mkdir(machineId, workspaceId, relPath, ulid()),
    retry: false,
    onSettled: () => refreshFiles(queryClient, machineId, workspaceId),
  })
}

export function useDeleteFile(machineId: string, workspaceId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ relPath }: { relPath: string }) =>
      deleteFile(machineId, workspaceId, relPath, ulid()),
    retry: false,
    onSettled: () => refreshFiles(queryClient, machineId, workspaceId),
  })
}

export function useRenameFile(machineId: string, workspaceId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ relPath, newName }: { relPath: string; newName: string }) =>
      renameFile(machineId, workspaceId, relPath, newName, ulid()),
    retry: false,
    onSettled: () => refreshFiles(queryClient, machineId, workspaceId),
  })
}

export type UploadProgress = {
  fileName: string
  sentBytes: number
  totalBytes: number
  phase: 'uploading' | 'applying' | 'done'
}

function sleep(ms: number) {
  return new Promise<void>((resolve) => window.setTimeout(resolve, ms))
}

export function useUploadFile(machineId: string, workspaceId: string) {
  const queryClient = useQueryClient()
  const [progress, setProgress] = useState<UploadProgress | null>(null)
  const mutation = useMutation({
    mutationFn: async ({
      file,
      relDir,
      conflict,
    }: {
      file: File
      relDir: string
      conflict: UploadConflict
    }) => {
      setProgress({
        fileName: file.name,
        sentBytes: 0,
        totalBytes: file.size,
        phase: 'uploading',
      })
      const created = await createUpload(machineId, workspaceId, {
        relDir,
        fileName: file.name,
        size: file.size,
        conflict,
      })
      const chunksTotal = file.size === 0 ? 0 : Math.ceil(file.size / created.chunkSize)
      let sent = 0
      for (let index = 0; index < chunksTotal; index += 1) {
        const start = index * created.chunkSize
        const chunk = file.slice(start, Math.min(start + created.chunkSize, file.size))
        await putTransferChunk(machineId, created.transferId, index, chunk)
        sent = Math.min(file.size, start + chunk.size)
        setProgress({
          fileName: file.name,
          sentBytes: sent,
          totalBytes: file.size,
          phase: 'uploading',
        })
      }
      setProgress({
        fileName: file.name,
        sentBytes: file.size,
        totalBytes: file.size,
        phase: 'applying',
      })
      let status = await completeTransfer(machineId, created.transferId)
      for (let attempt = 0; attempt < 20; attempt += 1) {
        if (status.status === 'completed' || status.status === 'failed' || status.status === 'expired') {
          break
        }
        await sleep(500)
        status = await fetchTransfer(machineId, created.transferId)
      }
      if (status.status === 'failed' || status.status === 'expired') {
        throw new Error(status.error ?? '머신이 업로드를 적용하지 못했습니다.')
      }
      setProgress({
        fileName: file.name,
        sentBytes: file.size,
        totalBytes: file.size,
        phase: 'done',
      })
      return created.transferId
    },
    retry: false,
    onSettled: () => refreshFiles(queryClient, machineId, workspaceId),
  })
  return { ...mutation, progress, clearProgress: () => setProgress(null) }
}

export function useDownloadFile(machineId: string, workspaceId: string) {
  const [preparing, setPreparing] = useState<string | null>(null)
  const mutation = useMutation({
    mutationFn: async ({ relPath, asZip }: { relPath: string; asZip: boolean }) => {
      setPreparing(relPath)
      const created = await createDownload(machineId, workspaceId, relPath, asZip)
      for (let attempt = 0; attempt < 120; attempt += 1) {
        const status = await fetchTransfer(machineId, created.transferId)
        if (status.status === 'ready') {
          return {
            url: transferContentUrl(machineId, created.transferId),
            fileName: relPath.split('/').filter(Boolean).pop() ?? 'download',
          }
        }
        if (status.status === 'failed' || status.status === 'expired') {
          throw new Error(status.error ?? '머신이 다운로드를 준비하지 못했습니다.')
        }
        await sleep(500)
      }
      throw new Error('다운로드 준비 시간이 초과되었습니다.')
    },
    retry: false,
    onSettled: () => setPreparing(null),
  })
  return { ...mutation, preparing }
}

export function useVpnPage(
  machineId: string | undefined,
  country: string | undefined,
  prevCity: string | undefined,
) {
  return useQuery({
    queryKey: queryKeys.vpnPage(machineId ?? '', country ?? '', prevCity ?? ''),
    queryFn: ({ signal }) => fetchVpnPage(machineId ?? '', country, prevCity, signal),
    enabled: Boolean(machineId),
    refetchInterval: (query) => {
      const status = query.state.data?.data.status
      return status?.busyOperation || status?.busyCommandId ? 2_500 : 30_000
    },
    refetchIntervalInBackground: false,
  })
}

export function useVpnConnect(machineId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (input: VpnConnectInput) => connectVpn(machineId, input, ulid()),
    retry: false,
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['vpn-page', machineId] }),
        queryClient.invalidateQueries({ queryKey: queryKeys.machine(machineId) }),
      ])
    },
  })
}

export function useVpnDisconnect(machineId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => disconnectVpn(machineId, ulid()),
    retry: false,
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['vpn-page', machineId] }),
        queryClient.invalidateQueries({ queryKey: queryKeys.machine(machineId) }),
      ])
    },
  })
}

export function useRefreshInstances(machineId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => {
      const requestId = ulid()
      return api.postCommand(apiPaths.refreshInstances(machineId), {
        query: { wait: 0 },
        requestId,
        idempotencyKey: requestId,
      })
    },
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.instances(machineId) }),
        queryClient.invalidateQueries({ queryKey: queryKeys.features(machineId) }),
        queryClient.invalidateQueries({ queryKey: queryKeys.machine(machineId) }),
      ])
    },
  })
}

type PromptVariables = {
  input: PromptInput
  requestId: string
  mode: 'prompt' | 'abort-and-prompt'
}

export function usePromptCommand(
  machineId: string,
  instanceId: string,
  sessionId: string,
) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ input, requestId, mode }: PromptVariables) =>
      mode === 'prompt'
        ? issuePrompt(machineId, instanceId, sessionId, input, requestId)
        : issueAbortAndPrompt(machineId, instanceId, sessionId, input, requestId),
    retry: false,
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: queryKeys.session(machineId, instanceId, sessionId),
        }),
        queryClient.invalidateQueries({
          queryKey: queryKeys.sessions(machineId, instanceId, 'active'),
        }),
        queryClient.invalidateQueries({ queryKey: queryKeys.commands({}) }),
      ])
    },
  })
}

export function useAbortCommand(machineId: string, instanceId: string, sessionId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => issueAbort(machineId, instanceId, sessionId, ulid()),
    retry: false,
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: queryKeys.session(machineId, instanceId, sessionId),
        }),
        queryClient.invalidateQueries({
          queryKey: queryKeys.sessions(machineId, instanceId, 'active'),
        }),
      ])
    },
  })
}

export function usePermissionReply(
  machineId: string,
  instanceId: string,
  sessionId: string,
) {
  return useMutation({
    mutationFn: ({
      permissionId,
      response,
    }: {
      permissionId: string
      response: 'once' | 'always' | 'reject'
    }) => replyPermission(machineId, instanceId, sessionId, permissionId, response),
    retry: false,
  })
}

export function useCreateEnrollmentCode() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: createEnrollmentCode,
    retry: false,
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.audit({}) })
    },
  })
}

export function useRevokeMachine() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: revokeMachine,
    retry: false,
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.machines }),
        queryClient.invalidateQueries({ queryKey: queryKeys.audit({}) }),
      ])
    },
  })
}

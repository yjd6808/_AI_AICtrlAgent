import { HttpResponse, http } from 'msw'
import type {
  ApiEnvelope,
  FileEntry,
  FilePreviewView,
  FileSort,
  ModelRef,
  SessionView,
  TransferStatusView,
  UploadConflict,
  VpnConnectInput,
  VpnPolicyUpdateInput,
  VpnServerRow,
} from '../api/types'
import {
  advanceCommand,
  getMockState,
  liveMeta,
  type MockCommand,
  type MockState,
  type MockVpnState,
  type MockWorkspaceFiles,
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

function normalizeRelPath(value: string | null | undefined): string {
  return (value ?? '')
    .replace(/\\/g, '/')
    .replace(/^\/+|\/+$/g, '')
    .replace(/\/{2,}/g, '/')
}

function parentOf(relPath: string): string {
  const index = relPath.lastIndexOf('/')
  return index < 0 ? '' : relPath.slice(0, index)
}

function nameOf(relPath: string): string {
  const index = relPath.lastIndexOf('/')
  return index < 0 ? relPath : relPath.slice(index + 1)
}

function findEntry(files: MockWorkspaceFiles, relPath: string): FileEntry | undefined {
  const parent = parentOf(relPath)
  const name = nameOf(relPath)
  return files.entries[parent]?.find((entry) => entry.name === name)
}

function fileSortOf(value: string | null): FileSort {
  return value === 'modified' || value === 'size' ? value : 'name'
}

function sortEntries(entries: FileEntry[], sort: FileSort): FileEntry[] {
  return [...entries].sort((left, right) => {
    if (left.isDir !== right.isDir) {
      return left.isDir ? -1 : 1
    }
    if (sort === 'modified') {
      return right.modified.localeCompare(left.modified)
    }
    if (sort === 'size') {
      return (right.size ?? 0) - (left.size ?? 0)
    }
    return left.name.localeCompare(right.name, 'ko')
  })
}

function removeFileState(files: MockWorkspaceFiles, relPath: string, isDir: boolean): void {
  const parent = parentOf(relPath)
  if (isDir) {
    const prefix = `${relPath}/`
    Object.keys(files.entries).forEach((key) => {
      if (key === relPath || key.startsWith(prefix)) {
        delete files.entries[key]
      }
    })
    Object.keys(files.texts).forEach((key) => {
      if (key === relPath || key.startsWith(prefix)) {
        delete files.texts[key]
      }
    })
    Object.keys(files.images).forEach((key) => {
      if (key === relPath || key.startsWith(prefix)) {
        delete files.images[key]
      }
    })
  } else {
    delete files.texts[relPath]
    delete files.images[relPath]
  }
  if (files.entries[parent]) {
    files.entries[parent] = files.entries[parent].filter((entry) => entry.name !== nameOf(relPath))
  }
}

function issueFileCommand(
  state: MockState,
  request: Request,
  machineId: string,
  type: string,
  delayMs = 650,
) {
  const command = startCommand(
    state,
    requestIdOf(request),
    machineId,
    type,
    'files',
    null,
    null,
    'succeeded',
    delayMs,
  )
  return HttpResponse.json(envelope(state, pending(command), machineId), { status: 202 })
}

function vpnStateOf(state: MockState, machineId: string): MockVpnState | undefined {
  return state.vpn[machineId]
}

function allVpnRows(vpn: MockVpnState): VpnServerRow[] {
  return Object.values(vpn.pages).flatMap((page) =>
    page.cities.flatMap((city) => city.servers),
  )
}

function boundedInt(value: number, min: number, max: number, fallback: number): number {
  const parsed = Number(value)
  if (!Number.isFinite(parsed)) {
    return fallback
  }
  return Math.max(min, Math.min(max, Math.trunc(parsed)))
}

function applyConnectedRow(vpn: MockVpnState, target: VpnServerRow): void {
  const now = new Date().toISOString()
  allVpnRows(vpn).forEach((row) => {
    if (row.rowState === 'Connected' && row.name !== target.name) {
      row.rowState = 'Recent'
      row.lastConnectedAt = row.lastConnectedAt ?? now
    }
  })
  target.rowState = 'Connected'
  target.lastConnectedAt = now
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

const workspaceHandlers = [
  http.get(`${API}/machines/:machineId/workspaces`, ({ params }) => {
    const state = getMockState()
    const machineId = String(params.machineId)
    const machine = machineOf(state, machineId)
    if (!machine) {
      return notFound('machine_not_found', '머신을 찾을 수 없습니다', machineId)
    }
    return HttpResponse.json(envelope(state, machine.workspaces, machineId))
  }),

  http.get(
    `${API}/machines/:machineId/workspaces/:workspaceId/files`,
    ({ params, request }) => {
      const state = getMockState()
      const machineId = String(params.machineId)
      const workspaceId = String(params.workspaceId)
      const files = state.files[workspaceId]
      if (!files) {
        return notFound('workspace_not_found', '워크스페이스를 찾을 수 없습니다', workspaceId)
      }
      const url = new URL(request.url)
      const relPath = normalizeRelPath(url.searchParams.get('path'))
      const sort = fileSortOf(url.searchParams.get('sort'))
      const cursor = Number(url.searchParams.get('cursor') ?? 0)
      const offset = Number.isFinite(cursor) && cursor > 0 ? cursor : 0
      const limit = Math.max(
        1,
        Math.min(500, Number(url.searchParams.get('limit') ?? 500)),
      )
      const rows = sortEntries(files.entries[relPath] ?? [], sort)
      const page = rows.slice(offset, offset + limit)
      const nextOffset = offset + page.length
      return HttpResponse.json(
        envelope(
          state,
          {
            entries: page,
            nextCursor: nextOffset < rows.length ? String(nextOffset) : null,
            hasMore: nextOffset < rows.length,
          },
          machineId,
        ),
      )
    },
  ),

  http.get(
    `${API}/machines/:machineId/workspaces/:workspaceId/files/stat`,
    ({ params, request }) => {
      const state = getMockState()
      const machineId = String(params.machineId)
      const workspaceId = String(params.workspaceId)
      const files = state.files[workspaceId]
      if (!files) {
        return notFound('workspace_not_found', '워크스페이스를 찾을 수 없습니다', workspaceId)
      }
      const relPath = normalizeRelPath(new URL(request.url).searchParams.get('path'))
      const entry = findEntry(files, relPath)
      if (!entry) {
        return notFound('file_not_found', '파일을 찾을 수 없습니다', relPath)
      }
      return HttpResponse.json(
        envelope(state, { ...entry, sha256: null }, machineId),
      )
    },
  ),

  http.get(
    `${API}/machines/:machineId/workspaces/:workspaceId/files/preview`,
    ({ params, request }) => {
      const state = getMockState()
      const machineId = String(params.machineId)
      const workspaceId = String(params.workspaceId)
      const files = state.files[workspaceId]
      if (!files) {
        return notFound('workspace_not_found', '워크스페이스를 찾을 수 없습니다', workspaceId)
      }
      const relPath = normalizeRelPath(new URL(request.url).searchParams.get('path'))
      const entry = findEntry(files, relPath)
      if (!entry) {
        return notFound('file_not_found', '파일을 찾을 수 없습니다', relPath)
      }
      if (entry.isDir) {
        return problem(400, 'invalid_request', '폴더는 미리보기할 수 없습니다', relPath)
      }
      if (entry.denied) {
        return problem(403, 'access_denied', '파일 접근이 거부되었습니다', relPath)
      }
      const image = files.images[relPath]
      const text = files.texts[relPath]
      const kind: FilePreviewView['kind'] = text ? 'text' : image ? 'image' : 'binary'
      const truncated = Boolean(text && text.length > 32_000)
      const preview: FilePreviewView = {
        name: entry.name,
        relPath,
        kind,
        size: entry.size ?? 0,
        modified: entry.modified,
        text: text ? (truncated ? text.slice(0, 32_000) : text) : null,
        base64: image?.base64 ?? null,
        truncated,
      }
      return HttpResponse.json(envelope(state, preview, machineId))
    },
  ),

  http.post(
    `${API}/machines/:machineId/workspaces/:workspaceId/files/mkdir`,
    async ({ params, request }) => {
      const state = getMockState()
      const machineId = String(params.machineId)
      const workspaceId = String(params.workspaceId)
      const files = state.files[workspaceId]
      if (!files) {
        return notFound('workspace_not_found', '워크스페이스를 찾을 수 없습니다', workspaceId)
      }
      const body = (await request.json()) as { relPath?: string }
      const relPath = normalizeRelPath(body.relPath)
      if (!relPath) {
        return problem(400, 'invalid_request', '폴더 경로가 비어 있습니다')
      }
      const parent = parentOf(relPath)
      if (!files.entries[parent]) {
        return notFound('file_not_found', '상위 폴더를 찾을 수 없습니다', parent)
      }
      if (findEntry(files, relPath)) {
        return problem(409, 'file_exists', '같은 이름의 항목이 이미 있습니다', relPath)
      }
      const name = nameOf(relPath)
      files.entries[parent] = [
        ...(files.entries[parent] ?? []),
        {
          name,
          relPath,
          isDir: true,
          size: null,
          modified: new Date().toISOString(),
          hidden: name.startsWith('.'),
          denied: false,
          isReparsePoint: false,
        },
      ]
      return issueFileCommand(state, request, machineId, 'files.mkdir')
    },
  ),

  http.post(
    `${API}/machines/:machineId/workspaces/:workspaceId/files/delete`,
    async ({ params, request }) => {
      const state = getMockState()
      const machineId = String(params.machineId)
      const workspaceId = String(params.workspaceId)
      const files = state.files[workspaceId]
      if (!files) {
        return notFound('workspace_not_found', '워크스페이스를 찾을 수 없습니다', workspaceId)
      }
      const body = (await request.json()) as { relPath?: string }
      const relPath = normalizeRelPath(body.relPath)
      const entry = findEntry(files, relPath)
      if (!entry) {
        return notFound('file_not_found', '파일을 찾을 수 없습니다', relPath)
      }
      removeFileState(files, relPath, entry.isDir)
      return issueFileCommand(state, request, machineId, 'files.delete')
    },
  ),

  http.post(
    `${API}/machines/:machineId/workspaces/:workspaceId/files/rename`,
    async ({ params, request }) => {
      const state = getMockState()
      const machineId = String(params.machineId)
      const workspaceId = String(params.workspaceId)
      const files = state.files[workspaceId]
      if (!files) {
        return notFound('workspace_not_found', '워크스페이스를 찾을 수 없습니다', workspaceId)
      }
      const body = (await request.json()) as { relPath?: string; newName?: string }
      const relPath = normalizeRelPath(body.relPath)
      const newName = (body.newName ?? '').trim()
      if (!newName || /[\\/]/.test(newName)) {
        return problem(400, 'invalid_request', '새 이름이 올바르지 않습니다', newName)
      }
      const entry = findEntry(files, relPath)
      if (!entry) {
        return notFound('file_not_found', '파일을 찾을 수 없습니다', relPath)
      }
      const parent = parentOf(relPath)
      const newPath = parent ? `${parent}/${newName}` : newName
      if (newPath !== relPath && findEntry(files, newPath)) {
        return problem(409, 'file_exists', '같은 이름의 항목이 이미 있습니다', newPath)
      }
      if (entry.isDir) {
        const oldPrefix = `${relPath}/`
        Object.keys(files.entries).forEach((key) => {
          if (key !== relPath && !key.startsWith(oldPrefix)) {
            return
          }
          const nextKey = newPath + key.slice(relPath.length)
          const rows = files.entries[key].map((row) => ({
            ...row,
            relPath: newPath + row.relPath.slice(relPath.length),
          }))
          delete files.entries[key]
          files.entries[nextKey] = rows
        })
        delete files.entries[relPath]
      }
      const parentRows = files.entries[parent] ?? []
      const target = parentRows.find((row) => row.name === entry.name)
      if (target) {
        target.name = newName
        target.relPath = newPath
        target.modified = new Date().toISOString()
      }
      const rekey = (record: Record<string, unknown>) => {
        Object.keys(record).forEach((key) => {
          if (key !== relPath && !key.startsWith(`${relPath}/`)) {
            return
          }
          const nextKey = newPath + key.slice(relPath.length)
          record[nextKey] = record[key]
          delete record[key]
        })
      }
      rekey(files.texts)
      rekey(files.images)
      return issueFileCommand(state, request, machineId, 'files.rename')
    },
  ),

  http.post(
    `${API}/machines/:machineId/workspaces/:workspaceId/transfers/uploads`,
    async ({ params, request }) => {
      const state = getMockState()
      const machineId = String(params.machineId)
      const workspaceId = String(params.workspaceId)
      const files = state.files[workspaceId]
      if (!files) {
        return notFound('workspace_not_found', '워크스페이스를 찾을 수 없습니다', workspaceId)
      }
      const body = (await request.json()) as {
        relDir?: string
        fileName?: string
        size?: number
        conflict?: UploadConflict
      }
      const relDir = normalizeRelPath(body.relDir)
      let fileName = (body.fileName ?? '').trim()
      if (!fileName || /[\\/]/.test(fileName)) {
        return problem(400, 'invalid_request', '파일 이름이 올바르지 않습니다', fileName)
      }
      if (!files.entries[relDir]) {
        return notFound('file_not_found', '대상 폴더를 찾을 수 없습니다', relDir)
      }
      const conflict = body.conflict ?? 'fail'
      let relPath = relDir ? `${relDir}/${fileName}` : fileName
      const existing = findEntry(files, relPath)
      if (existing) {
        if (conflict === 'fail') {
          return problem(409, 'file_exists', '같은 이름의 파일이 이미 있습니다', relPath)
        }
        if (conflict === 'overwrite') {
          if (existing.isDir) {
            return problem(409, 'file_exists', '폴더는 덮어쓸 수 없습니다', relPath)
          }
          removeFileState(files, relPath, false)
        } else {
          const dot = fileName.lastIndexOf('.')
          const stem = dot > 0 ? fileName.slice(0, dot) : fileName
          const ext = dot > 0 ? fileName.slice(dot) : ''
          let index = 1
          while (findEntry(files, relDir ? `${relDir}/${fileName}` : fileName)) {
            fileName = `${stem} (${index})${ext}`
            index += 1
          }
          relPath = relDir ? `${relDir}/${fileName}` : fileName
        }
      }
      const size = Math.max(0, Number(body.size) || 0)
      const chunkSize = 64 * 1024
      const transferId = `tr_up_${Date.now().toString(36)}_${Math.random()
        .toString(36)
        .slice(2, 7)}`
      const expiresAt = new Date(Date.now() + 60 * 60 * 1000).toISOString()
      const transfer: TransferStatusView = {
        transferId,
        direction: 'up',
        machineId,
        workspaceId,
        relPath,
        size,
        sha256: null,
        asZip: false,
        conflict,
        chunkSize,
        chunksTotal: size === 0 ? 0 : Math.ceil(size / chunkSize),
        receivedChunks: [],
        status: 'uploading',
        createdAt: new Date().toISOString(),
        expiresAt,
        error: null,
      }
      state.transfers[transferId] = transfer
      return HttpResponse.json(
        envelope(state, { transferId, chunkSize, expiresAt }, machineId),
        { status: 201 },
      )
    },
  ),

  http.put(
    `${API}/machines/:machineId/transfers/:transferId/chunks/:index`,
    ({ params }) => {
      const state = getMockState()
      const machineId = String(params.machineId)
      const transfer = state.transfers[String(params.transferId)]
      if (!transfer || transfer.machineId !== machineId) {
        return notFound(
          'transfer_not_found',
          '전송을 찾을 수 없습니다',
          String(params.transferId),
        )
      }
      const index = Number(params.index)
      if (!Number.isInteger(index) || index < 0 || index >= transfer.chunksTotal) {
        return problem(400, 'invalid_chunk', '청크 번호가 범위를 벗어났습니다', String(index))
      }
      if (!transfer.receivedChunks.includes(index)) {
        transfer.receivedChunks = [...transfer.receivedChunks, index].sort((a, b) => a - b)
      }
      transfer.status = 'uploading'
      return HttpResponse.json(envelope(state, transfer, machineId))
    },
  ),

  http.post(
    `${API}/machines/:machineId/transfers/:transferId/complete`,
    ({ params }) => {
      const state = getMockState()
      const machineId = String(params.machineId)
      const transfer = state.transfers[String(params.transferId)]
      if (!transfer || transfer.machineId !== machineId) {
        return notFound(
          'transfer_not_found',
          '전송을 찾을 수 없습니다',
          String(params.transferId),
        )
      }
      const files = state.files[transfer.workspaceId]
      if (transfer.direction === 'up' && files) {
        const parent = parentOf(transfer.relPath)
        const name = nameOf(transfer.relPath)
        files.entries[parent] = [
          ...(files.entries[parent] ?? []).filter((entry) => entry.name !== name),
          {
            name,
            relPath: transfer.relPath,
            isDir: false,
            size: transfer.size,
            modified: new Date().toISOString(),
            hidden: name.startsWith('.'),
            denied: false,
            isReparsePoint: false,
          },
        ]
      }
      transfer.status = 'completed'
      transfer.error = null
      return HttpResponse.json(envelope(state, transfer, machineId))
    },
  ),

  http.get(`${API}/machines/:machineId/transfers/:transferId`, ({ params }) => {
    const state = getMockState()
    const machineId = String(params.machineId)
    const transfer = state.transfers[String(params.transferId)]
    if (!transfer || transfer.machineId !== machineId) {
      return notFound(
        'transfer_not_found',
        '전송을 찾을 수 없습니다',
        String(params.transferId),
      )
    }
    return HttpResponse.json(envelope(state, transfer, machineId))
  }),

  http.post(
    `${API}/machines/:machineId/transfers/:transferId/dispatch`,
    ({ params }) => {
      const state = getMockState()
      const machineId = String(params.machineId)
      const transfer = state.transfers[String(params.transferId)]
      if (!transfer || transfer.machineId !== machineId) {
        return notFound(
          'transfer_not_found',
          '전송을 찾을 수 없습니다',
          String(params.transferId),
        )
      }
      return HttpResponse.json(
        envelope(
          state,
          {
            transferId: transfer.transferId,
            commandId: `cmd_transfer_${transfer.transferId}`,
            status: 'Queued',
            pending: true,
          },
          machineId,
        ),
        { status: 202 },
      )
    },
  ),

  http.post(
    `${API}/machines/:machineId/workspaces/:workspaceId/transfers/downloads`,
    async ({ params, request }) => {
      const state = getMockState()
      const machineId = String(params.machineId)
      const workspaceId = String(params.workspaceId)
      const files = state.files[workspaceId]
      if (!files) {
        return notFound('workspace_not_found', '워크스페이스를 찾을 수 없습니다', workspaceId)
      }
      const body = (await request.json()) as { relPath?: string; asZip?: boolean }
      const relPath = normalizeRelPath(body.relPath)
      const entry = findEntry(files, relPath)
      if (!entry) {
        return notFound('file_not_found', '파일을 찾을 수 없습니다', relPath)
      }
      const asZip = Boolean(body.asZip)
      if (entry.isDir && !asZip) {
        return problem(400, 'invalid_request', '폴더는 ZIP으로만 내려받을 수 있습니다', relPath)
      }
      const transferId = `tr_down_${Date.now().toString(36)}_${Math.random()
        .toString(36)
        .slice(2, 7)}`
      const expiresAt = new Date(Date.now() + 60 * 60 * 1000).toISOString()
      const transfer: TransferStatusView = {
        transferId,
        direction: 'down',
        machineId,
        workspaceId,
        relPath,
        size: entry.size ?? 0,
        sha256: null,
        asZip,
        conflict: null,
        chunkSize: 64 * 1024,
        chunksTotal: 0,
        receivedChunks: [],
        status: 'ready',
        createdAt: new Date().toISOString(),
        expiresAt,
        error: null,
      }
      state.transfers[transferId] = transfer
      return HttpResponse.json(
        envelope(state, { transferId, chunkSize: transfer.chunkSize, expiresAt }, machineId),
        { status: 201 },
      )
    },
  ),

  http.get(
    `${API}/machines/:machineId/transfers/:transferId/content`,
    ({ params }) => {
      const state = getMockState()
      const machineId = String(params.machineId)
      const transfer = state.transfers[String(params.transferId)]
      if (!transfer || transfer.machineId !== machineId) {
        return notFound(
          'transfer_not_found',
          '전송을 찾을 수 없습니다',
          String(params.transferId),
        )
      }
      if (transfer.direction !== 'down' || transfer.status !== 'ready') {
        return problem(409, 'transfer_not_ready', '내려받을 수 없는 상태입니다', transfer.status)
      }
      const files = state.files[transfer.workspaceId]
      const name = nameOf(transfer.relPath)
      const headers = {
        'Content-Disposition': `attachment; filename="${name}"`,
      }
      if (transfer.asZip) {
        return new HttpResponse(`MOCK ZIP ${transfer.relPath}\n`, {
          headers: { ...headers, 'Content-Type': 'application/zip' },
        })
      }
      const image = files?.images[transfer.relPath]
      if (image) {
        const bytes = Uint8Array.from(atob(image.base64), (char) => char.charCodeAt(0))
        return new HttpResponse(bytes, {
          headers: { ...headers, 'Content-Type': image.mime },
        })
      }
      const text = files?.texts[transfer.relPath]
      return new HttpResponse(text ?? `Mock content for ${transfer.relPath}`, {
        headers: { ...headers, 'Content-Type': 'text/plain; charset=utf-8' },
      })
    },
  ),
]

const vpnHandlers = [
  http.get(`${API}/machines/:machineId/vpn/policy`, ({ params }) => {
    const state = getMockState()
    const machineId = String(params.machineId)
    const vpn = vpnStateOf(state, machineId)
    if (!vpn) {
      return problem(503, 'vpn_not_available', 'VPN을 사용할 수 없습니다', machineId)
    }
    vpn.policy.currentServer = vpn.status.server
    return HttpResponse.json(envelope(state, vpn.policy, machineId))
  }),

  http.put(
    `${API}/machines/:machineId/vpn/policy`,
    async ({ params, request }) => {
      const state = getMockState()
      const machineId = String(params.machineId)
      const vpn = vpnStateOf(state, machineId)
      if (!vpn) {
        return problem(503, 'vpn_not_available', 'VPN을 사용할 수 없습니다', machineId)
      }
      const body = (await request.json().catch(() => ({}))) as VpnPolicyUpdateInput
      const requestId = requestIdOf(request)
      const now = new Date().toISOString()
      const minLoad = boundedInt(body.minLoad, 0, 100, vpn.policy.minLoad)
      vpn.policy = {
        ...vpn.policy,
        enabled: Boolean(body.enabled),
        minLoad,
        maxLoad: boundedInt(body.maxLoad, minLoad, 100, vpn.policy.maxLoad),
        excludeUsedWithinHours: boundedInt(
          body.excludeUsedWithinHours,
          0,
          24 * 365,
          vpn.policy.excludeUsedWithinHours,
        ),
        maxAttemptsPerSession: boundedInt(
          body.maxAttemptsPerSession,
          1,
          5,
          vpn.policy.maxAttemptsPerSession,
        ),
        retryWaitThresholdSeconds: boundedInt(
          body.retryWaitThresholdSeconds,
          1,
          24 * 60 * 60,
          vpn.policy.retryWaitThresholdSeconds,
        ),
        countries: Array.isArray(body.countries)
          ? body.countries
              .map((value) => String(value).trim().toUpperCase())
              .filter(Boolean)
          : vpn.policy.countries,
        currentServer: vpn.status.server,
        updatedAt: now,
      }
      const command = startCommand(
        state,
        requestId,
        machineId,
        'vpn.policy.set',
        'vpn',
        null,
        null,
        'succeeded',
        700,
      )
      return HttpResponse.json(envelope(state, pending(command), machineId), { status: 202 })
    },
  ),

  http.get(`${API}/machines/:machineId/vpn/page`, ({ params, request }) => {
    const state = getMockState()
    const machineId = String(params.machineId)
    const vpn = vpnStateOf(state, machineId)
    if (!vpn) {
      return problem(503, 'vpn_not_available', 'VPN을 사용할 수 없습니다', machineId)
    }
    const url = new URL(request.url)
    const requested = url.searchParams.get('country')
    const selectedCountry =
      vpn.countries.find((country) => country.code === requested)?.code ??
      vpn.status.country ??
      vpn.countries[0]?.code ??
      ''
    const actionText = vpn.status.busyOperation
      ? 'VPN 작업을 처리하는 중입니다.'
      : vpn.status.tunnelUp
        ? `${vpn.status.server ?? 'VPN'}에 연결되어 있습니다.`
        : 'VPN에 연결되어 있지 않습니다.'
    return HttpResponse.json(
      envelope(
        state,
        {
          status: vpn.status,
          countries: vpn.countries,
          selectedCountry,
          servers: vpn.pages[selectedCountry] ?? null,
          actionText,
        },
        machineId,
      ),
    )
  }),

  http.post(
    `${API}/machines/:machineId/vpn/connect`,
    async ({ params, request }) => {
      const state = getMockState()
      const machineId = String(params.machineId)
      const vpn = vpnStateOf(state, machineId)
      if (!vpn) {
        return problem(503, 'vpn_not_available', 'VPN을 사용할 수 없습니다', machineId)
      }
      const body = (await request.json().catch(() => ({}))) as VpnConnectInput
      let target: VpnServerRow | undefined
      if (body.server) {
        target = allVpnRows(vpn).find((row) => row.name === body.server)
      }
      if (!target) {
        const country = body.country ?? vpn.status.country ?? vpn.countries[0]?.code ?? ''
        const page = vpn.pages[country]
        if (!page) {
          return notFound('vpn_country_not_found', '국가를 찾을 수 없습니다', country)
        }
        const city =
          (body.city && page.cities.some((group) => group.city === body.city)
            ? body.city
            : page.suggestedCity) ??
          page.cities[0]?.city ??
          ''
        const group = page.cities.find((item) => item.city === city) ?? page.cities[0]
        target = [...(group?.servers ?? [])].sort((left, right) => left.score - right.score)[0]
      }
      if (!target) {
        return notFound('vpn_server_not_found', '서버를 찾을 수 없습니다', body.server ?? '')
      }
      const requestId = requestIdOf(request)
      const now = new Date().toISOString()
      applyConnectedRow(vpn, target)
      Object.values(vpn.pages).forEach((page) => {
        page.asOf = now
      })
      vpn.status = {
        ...vpn.status,
        tunnelUp: true,
        server: target.name,
        country: target.country,
        city: target.city,
        detail: `${target.name} · ${target.city ?? target.countryName} · 방금 연결`,
        exitIp: `203.0.113.${(target.name.length + target.load) % 250}`,
        statusText: '연결됨',
        busyOperation: 'vpn.connect',
        busyCommandId: requestId,
        asOf: now,
      }
      const command = startCommand(
        state,
        requestId,
        machineId,
        'vpn.connect',
        'vpn',
        null,
        null,
        'succeeded',
        1_100,
      )
      return HttpResponse.json(envelope(state, pending(command), machineId), { status: 202 })
    },
  ),

  http.post(`${API}/machines/:machineId/vpn/disconnect`, ({ params, request }) => {
    const state = getMockState()
    const machineId = String(params.machineId)
    const vpn = vpnStateOf(state, machineId)
    if (!vpn) {
      return problem(503, 'vpn_not_available', 'VPN을 사용할 수 없습니다', machineId)
    }
    const requestId = requestIdOf(request)
    const now = new Date().toISOString()
    allVpnRows(vpn).forEach((row) => {
      if (row.rowState === 'Connected') {
        row.rowState = 'Recent'
        row.lastConnectedAt = now
      }
    })
    Object.values(vpn.pages).forEach((page) => {
      page.asOf = now
    })
    vpn.status = {
      ...vpn.status,
      tunnelUp: false,
      server: null,
      country: null,
      city: null,
      detail: '연결되어 있지 않습니다.',
      exitIp: null,
      statusText: '연결 안 됨',
      busyOperation: 'vpn.disconnect',
      busyCommandId: requestId,
      asOf: now,
    }
    const command = startCommand(
      state,
      requestId,
      machineId,
      'vpn.disconnect',
      'vpn',
      null,
      null,
      'succeeded',
      900,
    )
    return HttpResponse.json(envelope(state, pending(command), machineId), { status: 202 })
  }),
]

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

  ...workspaceHandlers,

  ...vpnHandlers,

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

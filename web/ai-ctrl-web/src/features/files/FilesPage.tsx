import { useMemo, useRef, useState } from 'react'
import { useParams, useSearchParams } from 'react-router-dom'
import {
  ChevronRight,
  Download,
  File as FileIcon,
  Folder,
  FolderPlus,
  Pencil,
  RefreshCw,
  Trash2,
  Upload,
  X,
} from 'lucide-react'
import {
  useDeleteFile,
  useDownloadFile,
  useFilePreview,
  useFiles,
  useMachine,
  useMkdir,
  useRenameFile,
  useUploadFile,
  useWorkspaces,
} from '../../api/queries'
import { describeApiError } from '../../api/errors'
import { formatAbsolute, formatBytes, formatRelative } from '../../api/format'
import type { FileEntry, FileSort, UploadConflict, WorkspaceInfo } from '../../api/types'
import { Badge } from '../../components/Badge'
import { Button } from '../../components/Button'
import { ConfirmDialog } from '../../components/ConfirmDialog'
import { FreshnessBadge } from '../../components/FreshnessBadge'
import { InlineNotice } from '../../components/InlineNotice'
import { PageHeader } from '../../components/PageHeader'
import { TextPromptDialog } from '../../components/TextPromptDialog'
import { EmptyBlock, ErrorBlock, LoadingBlock } from '../../components/AsyncState'
import { useToast } from '../../components/Toast'
import { cx } from '../../lib/cx'
import styles from './FilesPage.module.css'

const sortLabels: Record<FileSort, string> = {
  name: '이름 ↑',
  modified: '수정 ↓',
  size: '크기 ↓',
}

function parseSort(value: string | null): FileSort {
  return value === 'modified' || value === 'size' ? value : 'name'
}

function joinPath(base: string, name: string): string {
  return base ? `${base.replace(/\/+$/, '')}/${name}` : name
}

function isValidName(name: string): boolean {
  return Boolean(name) && name !== '.' && name !== '..' && !name.includes('/') && !name.includes('\\')
}

function imageMime(name: string): string {
  const ext = name.split('.').pop()?.toLowerCase()
  switch (ext) {
    case 'png':
      return 'image/png'
    case 'jpg':
    case 'jpeg':
      return 'image/jpeg'
    case 'gif':
      return 'image/gif'
    case 'webp':
      return 'image/webp'
    case 'svg':
      return 'image/svg+xml'
    case 'bmp':
      return 'image/bmp'
    default:
      return 'application/octet-stream'
  }
}

export function FilesPage() {
  const { machineId } = useParams<{ machineId: string }>()
  const workspaces = useWorkspaces(machineId)
  const machine = useMachine(machineId)

  if (!machineId) {
    return <EmptyBlock title="머신을 선택하세요" description="사이드바에서 머신을 선택합니다." />
  }

  if (workspaces.isLoading) {
    return <LoadingBlock label="워크스페이스를 불러오는 중" />
  }

  if (workspaces.isError) {
    return <ErrorBlock error={workspaces.error} onRetry={() => void workspaces.refetch()} />
  }

  const list = workspaces.data?.data ?? []
  if (list.length === 0) {
    return (
      <div className={styles.page}>
        <PageHeader
          eyebrow="Files"
          title="워크스페이스 파일"
          description="머신에 등록된 워크스페이스가 없습니다."
        />
        <EmptyBlock
          title="등록된 워크스페이스가 없습니다"
          description="Client 설정에서 워크스페이스를 등록하면 이 화면에서 파일을 탐색할 수 있습니다."
        />
      </div>
    )
  }

  return (
    <FilesBrowser
      machineId={machineId}
      workspaces={list}
      machineOnline={machine.data?.data.online}
      meta={workspaces.data?.meta}
    />
  )
}

function FilesBrowser({
  machineId,
  workspaces,
  machineOnline,
  meta,
}: {
  machineId: string
  workspaces: WorkspaceInfo[]
  machineOnline: boolean | undefined
  meta: Parameters<typeof FreshnessBadge>[0]['meta']
}) {
  const toast = useToast()
  const [params, setParams] = useSearchParams()
  const [mkdirOpen, setMkdirOpen] = useState(false)
  const [renameTarget, setRenameTarget] = useState<FileEntry | null>(null)
  const [deleteTarget, setDeleteTarget] = useState<FileEntry | null>(null)
  const [conflict, setConflict] = useState<UploadConflict>('fail')
  const inputRef = useRef<HTMLInputElement>(null)

  const wsParam = params.get('ws') ?? ''
  const workspace = workspaces.find((item) => item.workspaceId === wsParam) ?? workspaces[0]
  const workspaceId = workspace.workspaceId
  const path = params.get('path') ?? ''
  const sort = parseSort(params.get('sort'))
  const previewPath = params.get('preview') ?? undefined

  const files = useFiles(machineId, workspaceId, path, sort)
  const preview = useFilePreview(machineId, workspaceId, previewPath)
  const mkdirMutation = useMkdir(machineId, workspaceId)
  const renameMutation = useRenameFile(machineId, workspaceId)
  const deleteMutation = useDeleteFile(machineId, workspaceId)
  const upload = useUploadFile(machineId, workspaceId)
  const download = useDownloadFile(machineId, workspaceId)

  const entries = useMemo(
    () => files.data?.pages.flatMap((page) => page.data.entries) ?? [],
    [files.data],
  )

  const updateParams = (patch: Record<string, string | null>) => {
    const next = new URLSearchParams(params)
    for (const [key, value] of Object.entries(patch)) {
      if (value === null || value === '') {
        next.delete(key)
      } else {
        next.set(key, value)
      }
    }
    setParams(next, { replace: true })
  }

  const openEntry = (entry: FileEntry) => {
    if (entry.denied) {
      return
    }
    if (entry.isDir) {
      updateParams({ path: entry.relPath, preview: null })
      return
    }
    updateParams({ preview: entry.relPath })
  }

  const changeWorkspace = (nextId: string) => {
    updateParams({ ws: nextId, path: null, preview: null })
  }

  const startDownload = (entry: FileEntry) => {
    download.mutate(
      { relPath: entry.relPath, asZip: entry.isDir },
      {
        onSuccess: ({ url, fileName }) => {
          const anchor = document.createElement('a')
          anchor.href = url
          anchor.download = fileName
          document.body.appendChild(anchor)
          anchor.click()
          anchor.remove()
          toast.notify('info', `${fileName} 다운로드를 시작했습니다.`)
        },
        onError: (error) => toast.notify('error', describeApiError(error).message),
      },
    )
  }

  const submitMkdir = (name: string) => {
    if (!isValidName(name)) {
      toast.notify('error', '폴더 이름에 경로 구분자를 사용할 수 없습니다.')
      return
    }
    mkdirMutation.mutate(
      { relPath: joinPath(path, name) },
      {
        onSuccess: (result) => {
          setMkdirOpen(false)
          toast.notify(
            result.data.kind === 'pending' ? 'info' : 'success',
            result.data.kind === 'pending'
              ? '폴더 생성 명령을 접수했습니다.'
              : '폴더를 만들었습니다.',
          )
        },
        onError: (error) => toast.notify('error', describeApiError(error).message),
      },
    )
  }

  const submitRename = (name: string) => {
    if (!renameTarget) {
      return
    }
    if (!isValidName(name)) {
      toast.notify('error', '이름에 경로 구분자를 사용할 수 없습니다.')
      return
    }
    renameMutation.mutate(
      { relPath: renameTarget.relPath, newName: name },
      {
        onSuccess: (result) => {
          setRenameTarget(null)
          updateParams({ preview: null })
          toast.notify(
            result.data.kind === 'pending' ? 'info' : 'success',
            result.data.kind === 'pending'
              ? '이름 변경 명령을 접수했습니다.'
              : '이름을 변경했습니다.',
          )
        },
        onError: (error) => toast.notify('error', describeApiError(error).message),
      },
    )
  }

  const confirmDelete = () => {
    if (!deleteTarget) {
      return
    }
    deleteMutation.mutate(
      { relPath: deleteTarget.relPath },
      {
        onSuccess: (result) => {
          const removed = deleteTarget
          setDeleteTarget(null)
          if (
            previewPath === removed.relPath ||
            (previewPath?.startsWith(`${removed.relPath}/`) ?? false)
          ) {
            updateParams({ preview: null })
          }
          toast.notify(
            result.data.kind === 'pending' ? 'info' : 'success',
            result.data.kind === 'pending'
              ? '삭제 명령을 접수했습니다.'
              : '삭제했습니다.',
          )
        },
        onError: (error) => toast.notify('error', describeApiError(error).message),
      },
    )
  }

  const onUpload = (file: File | undefined) => {
    if (!file) {
      return
    }
    upload.mutate(
      { file, relDir: path, conflict },
      {
        onSuccess: () => toast.notify('success', `${file.name} 업로드를 완료했습니다.`),
        onError: (error) => toast.notify('error', describeApiError(error).message),
      },
    )
  }

  const breadcrumbs = [
    { label: workspace.name, path: '' },
    ...path
      .split('/')
      .filter(Boolean)
      .map((segment, index, parts) => ({
        label: segment,
        path: parts.slice(0, index + 1).join('/'),
      })),
  ]

  const progress = upload.progress
  const progressPercent =
    progress && progress.totalBytes > 0
      ? Math.min(100, Math.round((progress.sentBytes / progress.totalBytes) * 100))
      : progress
        ? 100
        : 0

  return (
    <div className={styles.page}>
      <PageHeader
        eyebrow="Files"
        title="워크스페이스 파일"
        description="머신 워크스페이스를 탐색하고 파일을 업로드하거나 내려받습니다."
        meta={
          <div className={styles.headerMeta}>
            <FreshnessBadge meta={meta} />
            <Badge tone="neutral">{workspace.name}</Badge>
          </div>
        }
        actions={
          <Button
            tone="secondary"
            size="sm"
            icon={<RefreshCw size={15} aria-hidden="true" />}
            loading={files.isFetching && !files.isFetchingNextPage}
            onClick={() => void files.refetch()}
          >
            새로고침
          </Button>
        }
      />

      {machineOnline === false ? (
        <InlineNotice tone="warn" title="머신 오프라인">
          캐시된 목록만 표시될 수 있습니다. 파일 작업은 Client가 다시 연결된 뒤 적용됩니다.
        </InlineNotice>
      ) : null}

      <section className={styles.workspaceBar}>
        <label className={styles.workspaceSelect}>
          <span>워크스페이스</span>
          <select
            value={workspaceId}
            onChange={(event) => changeWorkspace(event.target.value)}
          >
            {workspaces.map((item) => (
              <option key={item.workspaceId} value={item.workspaceId}>
                {item.name} — {item.root}
              </option>
            ))}
          </select>
        </label>
        <div className={styles.permissions}>
          {workspace.allowUpload ? <Badge tone="info">업로드</Badge> : null}
          {workspace.allowDownload ? <Badge tone="info">다운로드</Badge> : null}
          {workspace.allowMkdir ? <Badge tone="info">새 폴더</Badge> : null}
          {workspace.allowDelete ? <Badge tone="info">삭제</Badge> : null}
          {!workspace.allowUpload &&
          !workspace.allowDownload &&
          !workspace.allowMkdir &&
          !workspace.allowDelete ? (
            <Badge tone="warn">읽기 전용</Badge>
          ) : null}
          {workspace.denyPatterns.length > 0 ? (
            <Badge tone="neutral" title={workspace.denyPatterns.join(', ')}>
              차단 규칙 {workspace.denyPatterns.length}
            </Badge>
          ) : null}
        </div>
      </section>

      <div className={styles.layout}>
        <section className={styles.listPanel}>
          <div className={styles.toolbar}>
            <nav className={styles.breadcrumbs} aria-label="경로">
              {breadcrumbs.map((crumb, index) => (
                <span key={crumb.path || 'root'} className={styles.crumb}>
                  {index > 0 ? (
                    <ChevronRight size={13} className={styles.crumbIcon} aria-hidden="true" />
                  ) : null}
                  <button
                    type="button"
                    className={styles.crumbButton}
                    onClick={() => updateParams({ path: crumb.path, preview: null })}
                  >
                    {crumb.label}
                  </button>
                </span>
              ))}
            </nav>

            <div className={styles.toolbarActions}>
              {workspace.allowMkdir ? (
                <Button
                  tone="secondary"
                  size="sm"
                  icon={<FolderPlus size={15} aria-hidden="true" />}
                  onClick={() => setMkdirOpen(true)}
                >
                  새 폴더
                </Button>
              ) : null}
              {workspace.allowUpload ? (
                <>
                  <select
                    className={styles.conflictSelect}
                    aria-label="업로드 충돌 처리"
                    value={conflict}
                    onChange={(event) => setConflict(event.target.value as UploadConflict)}
                  >
                    <option value="fail">충돌 시 중단</option>
                    <option value="rename">이름 바꾸기</option>
                    <option value="overwrite">덮어쓰기</option>
                  </select>
                  <Button
                    tone="primary"
                    size="sm"
                    icon={<Upload size={15} aria-hidden="true" />}
                    loading={upload.isPending}
                    onClick={() => inputRef.current?.click()}
                  >
                    업로드
                  </Button>
                  <input
                    ref={inputRef}
                    type="file"
                    aria-label="파일 업로드"
                    className={styles.fileInput}
                    onChange={(event) => {
                      onUpload(event.target.files?.[0])
                      event.target.value = ''
                    }}
                  />
                </>
              ) : null}
            </div>
          </div>

          {upload.isPending && progress ? (
            <div className={styles.progress} role="status">
              <div className={styles.progressText}>
                <span>
                  {progress.phase === 'applying'
                    ? `${progress.fileName} · 머신에 적용하는 중`
                    : `${progress.fileName} · ${formatBytes(progress.sentBytes)} / ${formatBytes(progress.totalBytes)}`}
                </span>
                <span>{progressPercent}%</span>
              </div>
              <div className={styles.progressTrack}>
                <div
                  className={cx(
                    styles.progressBar,
                    progress.phase === 'applying' && styles.progressApply,
                  )}
                  style={{ width: `${progressPercent}%` }}
                />
              </div>
            </div>
          ) : null}

          {download.preparing ? (
            <InlineNotice tone="info">
              {download.preparing.split('/').pop()} 다운로드를 준비하는 중입니다.
            </InlineNotice>
          ) : null}

          <div className={styles.tableScroll}>
            {files.isLoading ? (
              <LoadingBlock label="파일 목록을 불러오는 중" />
            ) : files.isError ? (
              <ErrorBlock error={files.error} onRetry={() => void files.refetch()} compact />
            ) : entries.length === 0 ? (
              <EmptyBlock
                title="폴더가 비어 있습니다"
                description="이 경로에 표시할 파일이나 폴더가 없습니다."
              />
            ) : (
              <table className={styles.table}>
                <thead>
                  <tr>
                    <th scope="col">
                      <button
                        type="button"
                        className={cx(styles.sortButton, sort === 'name' && styles.sortActive)}
                        onClick={() => updateParams({ sort: 'name' })}
                      >
                        {sortLabels.name}
                      </button>
                    </th>
                    <th scope="col" className={styles.sizeColumn}>
                      <button
                        type="button"
                        className={cx(styles.sortButton, sort === 'size' && styles.sortActive)}
                        onClick={() => updateParams({ sort: 'size' })}
                      >
                        {sortLabels.size}
                      </button>
                    </th>
                    <th scope="col" className={styles.modifiedColumn}>
                      <button
                        type="button"
                        className={cx(styles.sortButton, sort === 'modified' && styles.sortActive)}
                        onClick={() => updateParams({ sort: 'modified' })}
                      >
                        {sortLabels.modified}
                      </button>
                    </th>
                    <th scope="col" className={styles.actionColumn}>
                      <span className={styles.visuallyHidden}>작업</span>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {entries.map((entry) => (
                    <tr
                      key={entry.relPath}
                      className={cx(
                        previewPath === entry.relPath && styles.selectedRow,
                        entry.denied && styles.deniedRow,
                      )}
                    >
                      <td>
                        <button
                          type="button"
                          className={styles.nameButton}
                          onClick={() => openEntry(entry)}
                          disabled={entry.denied}
                          title={entry.denied ? '접근이 차단된 항목입니다.' : entry.name}
                        >
                          <span
                            className={cx(
                              styles.entryIcon,
                              entry.isDir ? styles.folderIcon : styles.fileIcon,
                            )}
                            aria-hidden="true"
                          >
                            {entry.isDir ? <Folder size={15} /> : <FileIcon size={15} />}
                          </span>
                          <span className={styles.entryName}>{entry.name}</span>
                          {entry.hidden ? <Badge tone="neutral">숨김</Badge> : null}
                          {entry.isReparsePoint ? <Badge tone="warn">링크</Badge> : null}
                          {entry.denied ? <Badge tone="danger">차단</Badge> : null}
                        </button>
                      </td>
                      <td className={styles.sizeColumn}>
                        {entry.isDir || entry.size === null ? '—' : formatBytes(entry.size)}
                      </td>
                      <td className={styles.modifiedColumn} title={formatAbsolute(entry.modified)}>
                        {formatRelative(entry.modified)}
                      </td>
                      <td className={styles.actionColumn}>
                        <div className={styles.rowActions}>
                          {workspace.allowDownload && !entry.denied ? (
                            <button
                              type="button"
                              className={styles.iconButton}
                              aria-label={`${entry.name} 다운로드`}
                              title={entry.isDir ? '폴더를 ZIP으로 다운로드' : '다운로드'}
                              disabled={
                                download.isPending &&
                                download.variables?.relPath === entry.relPath
                              }
                              onClick={() => startDownload(entry)}
                            >
                              <Download size={14} aria-hidden="true" />
                            </button>
                          ) : null}
                          {workspace.allowDelete && !entry.denied ? (
                            <>
                              <button
                                type="button"
                                className={styles.iconButton}
                                aria-label={`${entry.name} 이름 변경`}
                                title="이름 변경"
                                onClick={() => setRenameTarget(entry)}
                              >
                                <Pencil size={14} aria-hidden="true" />
                              </button>
                              <button
                                type="button"
                                className={cx(styles.iconButton, styles.dangerIcon)}
                                aria-label={`${entry.name} 삭제`}
                                title="삭제"
                                onClick={() => setDeleteTarget(entry)}
                              >
                                <Trash2 size={14} aria-hidden="true" />
                              </button>
                            </>
                          ) : null}
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>

          <div className={styles.listFooter}>
            <span>
              {entries.length}개 항목
              {files.hasNextPage ? ' · 더 있음' : ''}
            </span>
            {files.hasNextPage ? (
              <Button
                tone="ghost"
                size="sm"
                loading={files.isFetchingNextPage}
                onClick={() => void files.fetchNextPage()}
              >
                더 보기
              </Button>
            ) : null}
          </div>
        </section>

        {previewPath ? (
          <aside className={styles.previewPanel} aria-label="파일 미리보기">
            <div className={styles.previewHeader}>
              <div className={styles.previewTitleBlock}>
                <p className={styles.previewTitle}>
                  {preview.data?.data.name ?? previewPath.split('/').pop()}
                </p>
                <p className={styles.previewPath}>{previewPath}</p>
              </div>
              <button
                type="button"
                className={styles.iconButton}
                aria-label="미리보기 닫기"
                onClick={() => updateParams({ preview: null })}
              >
                <X size={16} aria-hidden="true" />
              </button>
            </div>

            {preview.isLoading ? (
              <LoadingBlock label="미리보기를 불러오는 중" />
            ) : preview.isError ? (
              <ErrorBlock
                error={preview.error}
                onRetry={() => void preview.refetch()}
                compact
              />
            ) : preview.data ? (
              <>
                <div className={styles.previewMeta}>
                  <span>{formatBytes(preview.data.data.size)}</span>
                  <span>{formatRelative(preview.data.data.modified)}</span>
                  {preview.data.data.truncated ? <Badge tone="warn">일부만 표시</Badge> : null}
                </div>
                <div className={styles.previewBody}>
                  {preview.data.data.kind === 'text' ? (
                    <pre className={styles.previewText}>{preview.data.data.text}</pre>
                  ) : preview.data.data.kind === 'image' && preview.data.data.base64 ? (
                    <img
                      className={styles.previewImage}
                      alt={preview.data.data.name}
                      src={`data:${imageMime(preview.data.data.name)};base64,${preview.data.data.base64}`}
                    />
                  ) : (
                    <div className={styles.previewBinary}>
                      <FileIcon size={24} aria-hidden="true" />
                      <p>미리보기를 지원하지 않는 형식입니다.</p>
                      {workspace.allowDownload ? (
                        <Button
                          tone="secondary"
                          size="sm"
                          icon={<Download size={15} aria-hidden="true" />}
                          onClick={() => {
                            const entry = entries.find((item) => item.relPath === previewPath)
                            if (entry) {
                              startDownload(entry)
                            }
                          }}
                        >
                          다운로드
                        </Button>
                      ) : null}
                    </div>
                  )}
                </div>
              </>
            ) : null}
          </aside>
        ) : null}
      </div>

      {mkdirOpen ? (
        <TextPromptDialog
          open
          title="새 폴더"
          description={`${path || workspace.name} 안에 폴더를 만듭니다.`}
          label="폴더 이름"
          placeholder="새 폴더"
          confirmLabel="만들기"
          busy={mkdirMutation.isPending}
          onSubmit={submitMkdir}
          onClose={() => setMkdirOpen(false)}
        />
      ) : null}

      {renameTarget ? (
        <TextPromptDialog
          open
          title="이름 변경"
          description={renameTarget.relPath}
          label="새 이름"
          initialValue={renameTarget.name}
          confirmLabel="변경"
          busy={renameMutation.isPending}
          onSubmit={submitRename}
          onClose={() => setRenameTarget(null)}
        />
      ) : null}

      {deleteTarget ? (
        <ConfirmDialog
          open
          destructive
          title={deleteTarget.isDir ? '폴더를 삭제할까요?' : '파일을 삭제할까요?'}
          description={
            <>
              <strong>{deleteTarget.name}</strong>
              {deleteTarget.isDir ? ' 폴더와 하위 항목이' : ' 파일이'} 삭제됩니다. 이 작업은
              되돌릴 수 없습니다.
            </>
          }
          confirmLabel="삭제"
          busy={deleteMutation.isPending}
          onConfirm={confirmDelete}
          onClose={() => setDeleteTarget(null)}
        />
      ) : null}
    </div>
  )
}

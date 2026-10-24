import { useEffect, useState, type ReactNode } from 'react'
import { useQueries } from '@tanstack/react-query'
import {
  ChevronRight,
  Download,
  FileText,
  Folder,
  Monitor,
  Server,
  User,
  X,
} from 'lucide-react'
import { fetchMachineFs } from '../api/client'
import { describeApiError } from '../api/errors'
import { queryKeys, useMachineFs } from '../api/queries'
import type { FsEntry, FsQuickPath } from '../api/types'
import { cx } from '../lib/cx'
import { Button } from './Button'
import styles from './DirectoryPickerDialog.module.css'

type DirectoryPickerDialogProps = {
  machineId: string
  initialPath?: string
  onSelect: (path: string) => void
  onClose: () => void
}

const absolutePathPattern = /^(?:[A-Za-z]:[\\/]|\\\\)/

const quickPathLabels: Record<string, string> = {
  desktop: '바탕 화면',
  documents: '문서',
  downloads: '다운로드',
  profile: '사용자 폴더',
  appdata: 'AppData (Roaming)',
  localappdata: 'AppData (Local)',
}

function quickPathIcon(key: string) {
  switch (key) {
    case 'desktop':
      return Monitor
    case 'documents':
      return FileText
    case 'downloads':
      return Download
    case 'profile':
      return User
    default:
      return Folder
  }
}

function quickPathLabel(quick: FsQuickPath): string {
  return quickPathLabels[quick.key] ?? quick.path
}

export function DirectoryPickerDialog({
  machineId,
  initialPath = '',
  onSelect,
  onClose,
}: DirectoryPickerDialogProps) {
  const initial = absolutePathPattern.test(initialPath.trim())
    ? initialPath.trim()
    : ''
  const [expanded, setExpanded] = useState<string[]>(() => ancestorsOf(initial))
  const [current, setCurrent] = useState(initial)
  const [draft, setDraft] = useState(initialPath)
  const [error, setError] = useState<string | null>(null)
  const [checking, setChecking] = useState(false)

  useEffect(() => {
    const previous = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        onClose()
      }
    }
    window.addEventListener('keydown', onKeyDown)
    return () => {
      document.body.style.overflow = previous
      window.removeEventListener('keydown', onKeyDown)
    }
  }, [onClose])

  const roots = useMachineFs(machineId, null)
  const childQueries = useQueries({
    queries: expanded.map((path) => ({
      queryKey: queryKeys.machineFs(machineId, path),
      queryFn: ({ signal }: { signal: AbortSignal }) =>
        fetchMachineFs(machineId, path, signal),
      staleTime: 10_000,
      retry: false,
    })),
  })

  const childQueriesByPath = new Map<
    string,
    (typeof childQueries)[number]
  >()
  expanded.forEach((path, index) => {
    const query = childQueries[index]
    if (query) {
      childQueriesByPath.set(path, query)
    }
  })

  const toggle = (path: string) => {
    setExpanded((list) =>
      list.includes(path)
        ? list.filter((item) => item !== path)
        : [...list, path],
    )
  }

  const select = (path: string) => {
    setCurrent(path)
    setDraft(path)
    setError(null)
  }

  const navigate = async (raw: string) => {
    const target = raw.trim()
    if (!target) {
      setError('경로를 입력하세요.')
      return
    }

    if (!absolutePathPattern.test(target)) {
      setError('C:\\... 또는 \\\\서버\\공유 형식의 절대 경로를 입력하세요.')
      return
    }

    setChecking(true)
    setError(null)
    try {
      const result = await fetchMachineFs(machineId, target)
      const resolved = result.data.path ?? target
      select(resolved)
      setExpanded([...ancestorsOf(resolved), resolved])
    } catch (caught) {
      setError(describeApiError(caught).message)
    } finally {
      setChecking(false)
    }
  }

  function renderEntry(entry: FsEntry, depth: number): ReactNode {
    const isOpen = expanded.includes(entry.fullPath)
    const isSelected = current === entry.fullPath
    return (
      <div key={entry.fullPath} className={styles.node}>
        <div
          className={cx(styles.row, isSelected && styles.rowSelected)}
          style={{ paddingLeft: 6 + depth * 14 }}
        >
          <button
            type="button"
            className={cx(styles.chevron, isOpen && styles.chevronOpen)}
            onClick={() => toggle(entry.fullPath)}
            disabled={entry.denied}
            aria-label={isOpen ? '접기' : '펼치기'}
            aria-expanded={isOpen}
          >
            <ChevronRight size={13} aria-hidden="true" />
          </button>
          {depth === 0 ? (
            <Server size={14} className={styles.icon} aria-hidden="true" />
          ) : (
            <Folder size={14} className={styles.icon} aria-hidden="true" />
          )}
          <button
            type="button"
            className={styles.name}
            onClick={() => {
              if (entry.denied) {
                return
              }

              select(entry.fullPath)
              if (!isOpen) {
                toggle(entry.fullPath)
              }
            }}
            disabled={entry.denied}
            title={entry.fullPath}
          >
            {entry.name}
          </button>
          {entry.denied ? <span className={styles.denied}>차단됨</span> : null}
        </div>
        {isOpen && !entry.denied
          ? renderChildren(entry.fullPath, depth + 1)
          : null}
      </div>
    )
  }

  function renderQuickPath(quick: FsQuickPath): ReactNode {
    const isOpen = expanded.includes(quick.path)
    const isSelected = current === quick.path
    const Icon = quickPathIcon(quick.key)
    return (
      <div key={`quick:${quick.path}`} className={styles.node}>
        <div
          className={cx(styles.row, isSelected && styles.rowSelected)}
          style={{ paddingLeft: 6 }}
        >
          <button
            type="button"
            className={cx(styles.chevron, isOpen && styles.chevronOpen)}
            onClick={() => toggle(quick.path)}
            aria-label={isOpen ? '접기' : '펼치기'}
            aria-expanded={isOpen}
          >
            <ChevronRight size={13} aria-hidden="true" />
          </button>
          <Icon size={14} className={styles.icon} aria-hidden="true" />
          <button
            type="button"
            className={styles.name}
            onClick={() => {
              select(quick.path)
              if (!isOpen) {
                toggle(quick.path)
              }
            }}
            title={quick.path}
          >
            {quickPathLabel(quick)}
          </button>
          <code className={styles.quickPath}>{quick.path}</code>
        </div>
        {isOpen ? renderChildren(quick.path, 1) : null}
      </div>
    )
  }

  function renderChildren(path: string, depth: number): ReactNode {
    const query = childQueriesByPath.get(path)
    if (!query) {
      return null
    }

    const indent = { paddingLeft: 24 + depth * 14 }
    if (query.isPending) {
      return (
        <p className={styles.status} style={indent}>
          불러오는 중…
        </p>
      )
    }

    if (query.isError) {
      return (
        <p className={styles.statusError} style={indent}>
          {describeApiError(query.error).message}
        </p>
      )
    }

    const entries = query.data?.data.entries ?? []
    if (entries.length === 0) {
      return (
        <p className={styles.status} style={indent}>
          하위 폴더가 없습니다.
        </p>
      )
    }

    return <>{entries.map((entry) => renderEntry(entry, depth))}</>
  }

  const quickPaths = (roots.data?.data.quickPaths ?? []).filter((quick) =>
    absolutePathPattern.test(quick.path),
  )

  const driveEntries: FsEntry[] = (roots.data?.data.drives ?? []).map(
    (drive) => ({
      name: drive.label ? `${drive.name} ${drive.label}` : drive.name,
      fullPath: drive.path,
      isDir: true,
      denied: false,
      isReparsePoint: false,
    }),
  )

  return (
    <div
      className={styles.backdrop}
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) {
          onClose()
        }
      }}
    >
      <div
        className={styles.dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby="directory-picker-title"
      >
        <div className={styles.header}>
          <h2 id="directory-picker-title" className={styles.title}>
            폴더 선택
          </h2>
          <button
            type="button"
            className={styles.close}
            aria-label="대화상자 닫기"
            onClick={onClose}
          >
            <X size={17} aria-hidden="true" />
          </button>
        </div>

        <p className={styles.hint}>
          바탕 화면, 문서, AppData 같은 자주 쓰는 위치와 드라이브를 트리
          루트로 표시합니다. 경로를 직접 입력해 이동할 수도 있습니다.
        </p>

        <form
          className={styles.pathBar}
          onSubmit={(event) => {
            event.preventDefault()
            void navigate(draft)
          }}
        >
          <input
            className={styles.pathInput}
            value={draft}
            onChange={(event) => setDraft(event.target.value)}
            placeholder="C:\Users\..."
            spellCheck={false}
            aria-label="경로 직접 입력"
          />
          <Button type="submit" size="sm" loading={checking}>
            이동
          </Button>
          <Button
            size="sm"
            tone="ghost"
            disabled={!current}
            onClick={() => {
              const parent = parentOf(current)
              if (parent) {
                void navigate(parent)
              }
            }}
          >
            상위
          </Button>
        </form>

        {error ? (
          <p className={styles.error} role="alert">
            {error}
          </p>
        ) : null}

        <div className={styles.tree}>
          {roots.isPending ? (
            <p className={styles.status}>위치 목록을 불러오는 중…</p>
          ) : null}
          {roots.isError ? (
            <p className={styles.statusError}>
              {describeApiError(roots.error).message}
            </p>
          ) : null}
          {quickPaths.length > 0 ? (
            <div className={styles.group} role="group" aria-label="빠른 위치">
              <p className={styles.groupTitle}>빠른 위치</p>
              {quickPaths.map((quick) => renderQuickPath(quick))}
            </div>
          ) : null}
          {driveEntries.length > 0 ? (
            <div className={styles.group} role="group" aria-label="드라이브">
              <p className={styles.groupTitle}>드라이브</p>
              {driveEntries.map((entry) => renderEntry(entry, 0))}
            </div>
          ) : null}
        </div>

        <div className={styles.selected}>
          <span className={styles.selectedLabel}>선택한 폴더</span>
          <code className={styles.selectedPath} aria-label="선택한 폴더 경로">
            {current || '아직 선택하지 않았습니다.'}
          </code>
        </div>

        <div className={styles.actions}>
          <Button tone="secondary" onClick={onClose}>
            취소
          </Button>
          <Button
            tone="primary"
            disabled={!current || checking}
            onClick={() => {
              onSelect(current)
              onClose()
            }}
          >
            이 폴더 선택
          </Button>
        </div>
      </div>
    </div>
  )
}

function splitPath(path: string): string[] {
  const normalized = path.trim().replace(/\//g, '\\')
  if (!normalized) {
    return []
  }

  const unc = /^\\\\([^\\]+)\\([^\\]+)/.exec(normalized)
  let prefix: string
  let rest: string
  if (unc) {
    prefix = `\\\\${unc[1]}\\${unc[2]}`
    rest = normalized.slice(unc[0].length)
  } else {
    const drive = /^([A-Za-z]:)\\?/.exec(normalized)
    if (!drive) {
      return []
    }

    prefix = `${drive[1]}\\`
    rest = normalized.slice(drive[0].length)
  }

  const parts = [prefix]
  let currentPath = prefix
  for (const part of rest.split('\\')) {
    if (!part) {
      continue
    }

    currentPath = currentPath.endsWith('\\')
      ? currentPath + part
      : `${currentPath}\\${part}`
    parts.push(currentPath)
  }

  return parts
}

function ancestorsOf(path: string): string[] {
  return splitPath(path).slice(0, -1)
}

function parentOf(path: string): string | null {
  const parts = splitPath(path)
  return parts.length > 1 ? (parts[parts.length - 2] ?? null) : null
}

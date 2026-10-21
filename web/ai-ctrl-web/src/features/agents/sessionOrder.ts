import type { SessionDisplayState, SessionSummary } from '../../api/types'

export const displayStatePriority: Record<SessionDisplayState, number> = {
  WaitingPermission: 1,
  Stalled: 2,
  QuotaBlocked: 3,
  Retrying: 4,
  Running: 5,
  ForeignBusy: 6,
  Errored: 7,
  Aborted: 8,
  Idle: 9,
  Unknown: 10,
}

export function sessionTime(session: SessionSummary): number {
  const value = session.lastActivityAt ?? session.updatedAt
  const time = new Date(value).getTime()
  return Number.isNaN(time) ? 0 : time
}

export function sortSessions(sessions: readonly SessionSummary[]): SessionSummary[] {
  return [...sessions].sort((left, right) => {
    const priority = displayStatePriority[left.displayState] - displayStatePriority[right.displayState]
    if (priority !== 0) {
      return priority
    }
    return sessionTime(right) - sessionTime(left)
  })
}

export type SessionRow = {
  session: SessionSummary
  depth: number
  hasChildren: boolean
  expanded: boolean
}

export function buildSessionRows(
  sessions: readonly SessionSummary[],
  expanded: ReadonlySet<string>,
): SessionRow[] {
  const ids = new Set(sessions.map((session) => session.ref.sessionId))
  const children = new Map<string, SessionSummary[]>()
  const roots: SessionSummary[] = []

  for (const session of sessions) {
    const parentId = session.parentId
    if (parentId && ids.has(parentId) && parentId !== session.ref.sessionId) {
      const bucket = children.get(parentId) ?? []
      bucket.push(session)
      children.set(parentId, bucket)
    } else {
      roots.push(session)
    }
  }

  const rows: SessionRow[] = []
  const visited = new Set<string>()

  const hideDescendants = (session: SessionSummary) => {
    if (visited.has(session.ref.sessionId)) {
      return
    }
    visited.add(session.ref.sessionId)
    for (const child of children.get(session.ref.sessionId) ?? []) {
      hideDescendants(child)
    }
  }

  const walk = (session: SessionSummary, depth: number) => {
    if (visited.has(session.ref.sessionId)) {
      return
    }
    visited.add(session.ref.sessionId)
    const childList = sortSessions(children.get(session.ref.sessionId) ?? [])
    const isExpanded = expanded.has(session.ref.sessionId)
    rows.push({
      session,
      depth,
      hasChildren: childList.length > 0,
      expanded: isExpanded,
    })
    if (!isExpanded) {
      for (const child of childList) {
        hideDescendants(child)
      }
      return
    }
    for (const child of childList) {
      walk(child, depth + 1)
    }
  }

  for (const root of sortSessions(roots)) {
    walk(root, 0)
  }

  for (const session of sortSessions(sessions)) {
    if (!visited.has(session.ref.sessionId)) {
      walk(session, 0)
    }
  }

  return rows
}

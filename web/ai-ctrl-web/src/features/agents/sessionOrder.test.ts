import { describe, expect, it } from 'vitest'
import type { SessionDisplayState, SessionSummary } from '../../api/types'
import { buildSessionRows, sortSessions } from './sessionOrder'

let counter = 0

function session(
  displayState: SessionDisplayState,
  minutesAgo: number,
  extra: Partial<SessionSummary> = {},
): SessionSummary {
  counter += 1
  const sessionId = `ses_${displayState}_${counter}`
  const at = new Date(Date.now() - minutesAgo * 60_000).toISOString()
  return {
    ref: {
      machine: { value: 'mach_alpha' },
      instance: { value: 'inst_alpha_opencode' },
      sessionId,
    },
    title: sessionId,
    parentId: null,
    directory: 'E:\\Programming\\AIProject',
    runState: 'Idle',
    lastError: 'None',
    lastErrorMessage: null,
    updatedAt: at,
    lastModel: null,
    retry: null,
    displayState,
    busySince: null,
    lastActivityAt: at,
    messageCount: 0,
    activeOnInstance: true,
    ...extra,
  }
}

describe('sortSessions', () => {
  it('sorts by display-state priority before recency', () => {
    const idleNew = session('Idle', 1)
    const waitingOld = session('WaitingPermission', 30)
    const running = session('Running', 5)
    const stalled = session('Stalled', 40)

    const sorted = sortSessions([idleNew, running, stalled, waitingOld])

    expect(sorted.map((item) => item.displayState)).toEqual([
      'WaitingPermission',
      'Stalled',
      'Running',
      'Idle',
    ])
  })

  it('sorts newest first within the same state', () => {
    const older = session('Running', 20)
    const newer = session('Running', 2)

    expect(sortSessions([older, newer])[0]).toBe(newer)
  })
})

describe('buildSessionRows', () => {
  it('hides children until the parent is expanded', () => {
    const parent = session('Running', 2)
    const child = session('Idle', 1, { parentId: parent.ref.sessionId })

    const collapsed = buildSessionRows([child, parent], new Set())
    expect(collapsed).toHaveLength(1)
    expect(collapsed[0].session).toBe(parent)
    expect(collapsed[0].hasChildren).toBe(true)

    const expanded = buildSessionRows(
      [child, parent],
      new Set([parent.ref.sessionId]),
    )
    expect(expanded).toHaveLength(2)
    expect(expanded[1].depth).toBe(1)
    expect(expanded[1].session).toBe(child)
  })

  it('promotes orphaned children to roots', () => {
    const orphan = session('Idle', 1, { parentId: 'ses_missing' })
    const rows = buildSessionRows([orphan], new Set())
    expect(rows).toHaveLength(1)
    expect(rows[0].depth).toBe(0)
  })
})

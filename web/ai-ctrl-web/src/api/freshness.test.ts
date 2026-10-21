import { describe, expect, it } from 'vitest'
import type { ApiMeta } from './types'
import { freshnessLabel, isStale } from './freshness'
import { formatBytes, formatElapsed, formatRelative } from './format'

function meta(overrides: Partial<ApiMeta> = {}): ApiMeta {
  return {
    source: 'live',
    asOf: new Date().toISOString(),
    machineOnline: true,
    stale: false,
    latencyMs: 18,
    traceId: 'trace_test',
    ...overrides,
  }
}

describe('freshness', () => {
  it('labels live and cached responses differently', () => {
    const now = Date.now()
    expect(freshnessLabel(meta(), now)).toBe('live')
    expect(
      freshnessLabel(
        meta({
          source: 'cache',
          stale: true,
          asOf: new Date(now - 7 * 60_000).toISOString(),
        }),
        now,
      ),
    ).toBe('7분 전 캐시')
  })

  it('treats cache source or stale flag as stale', () => {
    expect(isStale(meta({ source: 'cache' }))).toBe(true)
    expect(isStale(meta({ stale: true }))).toBe(true)
    expect(isStale(meta())).toBe(false)
    expect(isStale(null)).toBe(false)
  })
})

describe('format helpers', () => {
  it('formats relative times and elapsed durations', () => {
    const now = Date.now()
    expect(formatRelative(new Date(now - 20_000).toISOString(), now)).toBe('방금 전')
    expect(formatRelative(new Date(now - 5 * 60_000).toISOString(), now)).toBe('5분 전')
    expect(formatElapsed(new Date(now - 125 * 60_000).toISOString(), now)).toBe('2시간 5분')
  })

  it('formats byte sizes', () => {
    expect(formatBytes(512)).toBe('512 B')
    expect(formatBytes(1_536)).toBe('1.5 KB')
    expect(formatBytes(2 * 1024 * 1024)).toBe('2.0 MB')
  })
})

import type { ApiMeta } from './types'
import { formatAbsolute, minutesSince } from './format'

export function freshnessLabel(meta: ApiMeta | null | undefined, now = Date.now()): string {
  if (!meta) {
    return '—'
  }
  if (meta.source === 'live' && !meta.stale) {
    return 'live'
  }
  const minutes = minutesSince(meta.asOf, now) ?? 0
  if (minutes < 1) {
    return '방금 전 캐시'
  }
  return `${minutes}분 전 캐시`
}

export function freshnessTooltip(meta: ApiMeta | null | undefined): string {
  if (!meta) {
    return '신선도 정보 없음'
  }
  return [
    `source: ${meta.source}`,
    `asOf: ${formatAbsolute(meta.asOf)}`,
    `latency: ${meta.latencyMs}ms`,
    `machineOnline: ${meta.machineOnline}`,
    `stale: ${meta.stale}`,
    `traceId: ${meta.traceId}`,
  ].join('\n')
}

export function isStale(meta: ApiMeta | null | undefined): boolean {
  return Boolean(meta && (meta.stale || meta.source === 'cache'))
}

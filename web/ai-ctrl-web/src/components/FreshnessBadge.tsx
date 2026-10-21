import type { ApiMeta } from '../api/types'
import { freshnessLabel, freshnessTooltip, isStale } from '../api/freshness'
import { Badge } from './Badge'

export function FreshnessBadge({ meta }: { meta: ApiMeta | null | undefined }) {
  if (!meta) {
    return <Badge tone="neutral">신선도 없음</Badge>
  }
  const stale = isStale(meta)
  return (
    <Badge
      tone={stale ? 'warn' : 'ok'}
      title={freshnessTooltip(meta)}
    >
      {freshnessLabel(meta)}
      {!stale && meta.latencyMs > 0 ? ` · ${meta.latencyMs}ms` : ''}
    </Badge>
  )
}

const relativeFormatter = new Intl.RelativeTimeFormat('ko', { numeric: 'auto' })
const absoluteFormatter = new Intl.DateTimeFormat('ko-KR', {
  dateStyle: 'medium',
  timeStyle: 'medium',
})

export function parseDate(value: string | null | undefined): Date | null {
  if (!value) {
    return null
  }
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? null : date
}

export function formatRelative(value: string | null | undefined, now = Date.now()): string {
  const date = parseDate(value)
  if (!date) {
    return '—'
  }
  const diffSeconds = Math.round((date.getTime() - now) / 1000)
  const absolute = Math.abs(diffSeconds)
  if (absolute < 45) {
    return '방금 전'
  }
  if (absolute < 60 * 60) {
    return relativeFormatter.format(Math.round(diffSeconds / 60), 'minute')
  }
  if (absolute < 60 * 60 * 24) {
    return relativeFormatter.format(Math.round(diffSeconds / 3600), 'hour')
  }
  if (absolute < 60 * 60 * 24 * 30) {
    return relativeFormatter.format(Math.round(diffSeconds / 86400), 'day')
  }
  return absoluteFormatter.format(date)
}

export function formatAbsolute(value: string | null | undefined): string {
  const date = parseDate(value)
  return date ? absoluteFormatter.format(date) : '—'
}

export function formatElapsed(start: string | null | undefined, now = Date.now()): string {
  const date = parseDate(start)
  if (!date) {
    return '—'
  }
  const seconds = Math.max(0, Math.floor((now - date.getTime()) / 1000))
  if (seconds < 60) {
    return `${seconds}초`
  }
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) {
    return `${minutes}분`
  }
  const hours = Math.floor(minutes / 60)
  if (hours < 24) {
    return `${hours}시간 ${minutes % 60}분`
  }
  return `${Math.floor(hours / 24)}일 ${hours % 24}시간`
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`
  }
  if (bytes < 1024 * 1024) {
    return `${(bytes / 1024).toFixed(1)} KB`
  }
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

export function minutesSince(value: string | null | undefined, now = Date.now()): number | null {
  const date = parseDate(value)
  if (!date) {
    return null
  }
  return Math.max(0, Math.floor((now - date.getTime()) / 60_000))
}

export function shortId(value: string): string {
  return value.length > 12 ? `${value.slice(0, 8)}…${value.slice(-3)}` : value
}

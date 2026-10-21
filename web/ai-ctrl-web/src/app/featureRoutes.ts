import type { FeatureSection, InstanceInfo, MachineFeature } from '../api/types'
import { modeLabels, providerLabels } from '../api/labels'

export type SidebarItem = {
  key: string
  title: string
  href: string
  available: boolean
  badge: string | null
  unavailableReason: string | null
}

export type SidebarSection = {
  section: FeatureSection
  title: string
  items: SidebarItem[]
}

const globalRoutes = new Set(['/activity', '/commands', '/settings'])

function machineScoped(machineId: string, route: string): string {
  if (route.startsWith('/m/')) {
    return route
  }
  if (globalRoutes.has(route)) {
    return route
  }
  return `/m/${encodeURIComponent(machineId)}${route.startsWith('/') ? route : `/${route}`}`
}

function firstInstancePath(machineId: string, instances: readonly InstanceInfo[]): string {
  const first = instances[0]
  return first
    ? `/m/${encodeURIComponent(machineId)}/agents/${encodeURIComponent(first.id.value)}`
    : `/m/${encodeURIComponent(machineId)}`
}

export function normalizeFeatureRoute(
  feature: Pick<MachineFeature, 'key' | 'route'>,
  machineId: string,
  instances: readonly InstanceInfo[] = [],
): string | null {
  const key = feature.key.toLowerCase()
  const route = feature.route

  if (key === 'commands') {
    return '/commands'
  }
  if (key === 'sessions' || key.startsWith('agent:')) {
    return firstInstancePath(machineId, instances)
  }
  if (key === 'files') {
    return `/m/${encodeURIComponent(machineId)}/files`
  }
  if (key === 'vpn' || key.startsWith('vpn.')) {
    return `/m/${encodeURIComponent(machineId)}/vpn`
  }
  if (key === 'logs') {
    return `/m/${encodeURIComponent(machineId)}/logs`
  }
  if (key === 'diagnostics') {
    return `/m/${encodeURIComponent(machineId)}/diagnostics`
  }

  if (!route) {
    return null
  }
  if (route.startsWith('/m/')) {
    return route
  }
  if (globalRoutes.has(route)) {
    return route
  }
  if (route.startsWith('/')) {
    return machineScoped(machineId, route)
  }
  return null
}

function endpointLabel(instance: InstanceInfo): string {
  const endpoint = instance.endpoint
  try {
    const url = new URL(endpoint)
    const port = url.port || (url.protocol === 'https:' ? '443' : '80')
    return `${url.hostname}:${port}`
  } catch {
    return endpoint
  }
}

export function instanceLabel(instance: InstanceInfo): string {
  return `${providerLabels[instance.kind] ?? instance.kind} ${endpointLabel(instance)}`
}

export function buildSidebarSections(
  machineId: string,
  instances: readonly InstanceInfo[],
  features: readonly MachineFeature[],
): SidebarSection[] {
  const featureMap = new Map(features.map((feature) => [feature.key.toLowerCase(), feature]))

  const agentItems: SidebarItem[] = instances.map((instance) => ({
    key: `agent:${instance.id.value}`,
    title: instanceLabel(instance),
    href: `/m/${encodeURIComponent(machineId)}/agents/${encodeURIComponent(instance.id.value)}`,
    available: true,
    badge: instance.managed ? null : '읽기 전용',
    unavailableReason: instance.managed
      ? null
      : `${modeLabels[instance.mode] ?? instance.mode} 모드 — 읽기 전용`,
  }))

  const workspaceDefaults: SidebarItem[] = [
    {
      key: 'files',
      title: '파일',
      href: `/m/${encodeURIComponent(machineId)}/files`,
      available: false,
      badge: '준비 중',
      unavailableReason: '파일 API가 아직 서버에 없습니다.',
    },
  ]

  const etcDefaults: SidebarItem[] = [
    {
      key: 'vpn.protonvpn',
      title: 'ProtonVPN',
      href: `/m/${encodeURIComponent(machineId)}/vpn`,
      available: false,
      badge: '준비 중',
      unavailableReason: 'VPN API가 아직 서버에 없습니다.',
    },
    {
      key: 'logs',
      title: '서버 로그',
      href: `/m/${encodeURIComponent(machineId)}/logs`,
      available: true,
      badge: null,
      unavailableReason: null,
    },
    {
      key: 'diagnostics',
      title: '진단',
      href: `/m/${encodeURIComponent(machineId)}/diagnostics`,
      available: false,
      badge: '준비 중',
      unavailableReason: '진단 API가 아직 서버에 없습니다.',
    },
  ]

  const mergedWorkspace = mergeDefaults(workspaceDefaults, featureMap, machineId, instances)
  const mergedEtc = mergeDefaults(etcDefaults, featureMap, machineId, instances)

  const consumed = new Set([
    'sessions',
    'commands',
    'vpn',
    ...workspaceDefaults.map((item) => item.key),
    ...etcDefaults.map((item) => item.key),
  ])

  for (const feature of features) {
    const key = feature.key.toLowerCase()
    if (feature.section !== 'Workspace' && feature.section !== 'Etc') {
      continue
    }
    if (consumed.has(key)) {
      continue
    }
    const href = normalizeFeatureRoute(feature, machineId, instances)
    if (!href) {
      continue
    }
    const item: SidebarItem = {
      key,
      title: feature.title,
      href,
      available: feature.available,
      badge: feature.available ? feature.badge : (feature.badge ?? '준비 중'),
      unavailableReason: feature.unavailableReason,
    }
    if (feature.section === 'Workspace') {
      mergedWorkspace.push(item)
    } else {
      mergedEtc.push(item)
    }
  }

  return [
    { section: 'Agents', title: 'AI Agents', items: agentItems },
    { section: 'Workspace', title: 'Workspace', items: mergedWorkspace },
    { section: 'Etc', title: '기타', items: mergedEtc },
  ]
}

function mergeDefaults(
  defaults: SidebarItem[],
  featureMap: ReadonlyMap<string, MachineFeature>,
  machineId: string,
  instances: readonly InstanceInfo[],
): SidebarItem[] {
  return defaults.map((item) => {
    const feature =
      featureMap.get(item.key) ??
      (item.key === 'vpn.protonvpn' ? featureMap.get('vpn') : undefined)
    if (!feature) {
      return item
    }
    const href = normalizeFeatureRoute(feature, machineId, instances) ?? item.href
    return {
      key: item.key,
      title: feature.title || item.title,
      href,
      available: feature.available,
      badge: feature.available ? feature.badge : (feature.badge ?? '준비 중'),
      unavailableReason: feature.unavailableReason,
    }
  })
}

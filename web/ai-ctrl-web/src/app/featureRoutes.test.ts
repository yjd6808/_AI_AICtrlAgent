import { describe, expect, it } from 'vitest'
import type { InstanceInfo, MachineFeature } from '../api/types'
import { buildSidebarSections } from './featureRoutes'

function feature(
  key: string,
  section: MachineFeature['section'],
  title: string,
  route: string,
): MachineFeature {
  return {
    key,
    section,
    title,
    route,
    available: true,
    badge: null,
    unavailableReason: null,
  }
}

const instance: InstanceInfo = {
  id: { value: 'inst_one' },
  kind: 'OpenCode',
  endpoint: 'http://127.0.0.1:4096',
  mode: 'SharedStore',
  managed: false,
  healthy: true,
  version: '1.0.0',
  capabilities: '',
}

describe('buildSidebarSections', () => {
  it('merges vpn features without duplicating the default item', () => {
    const sections = buildSidebarSections('mach_one', [instance], [
      feature('sessions', 'Agents', 'Sessions', '/sessions'),
      feature('commands', 'Agents', 'Commands', '/commands'),
      feature('files', 'Workspace', '파일', '/files'),
      feature('vpn', 'Etc', 'ProtonVPN', '/vpn'),
      feature('logs', 'Etc', '서버 로그', '/logs'),
      feature('diagnostics', 'Etc', '진단', '/diagnostics'),
    ])

    const workspace = sections.find((section) => section.section === 'Workspace')
    const etc = sections.find((section) => section.section === 'Etc')

    expect(workspace?.items.map((item) => item.key)).toEqual(['files'])
    expect(etc?.items.map((item) => item.key)).toEqual([
      'vpn.protonvpn',
      'logs',
      'diagnostics',
    ])
  })

  it('marks unmanaged instances as read-only', () => {
    const sections = buildSidebarSections('mach_one', [instance], [])
    const agents = sections.find((section) => section.section === 'Agents')

    expect(agents?.items[0].badge).toBe('읽기 전용')
    expect(agents?.items[0].href).toBe('/m/mach_one/agents/inst_one')
  })
})

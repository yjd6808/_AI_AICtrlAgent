import { useEffect, useMemo, useState } from 'react'
import { useParams } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import {
  Activity,
  Check,
  Globe2,
  MapPin,
  Power,
  PowerOff,
  RefreshCw,
  Search,
  Shield,
  ShieldOff,
} from 'lucide-react'
import {
  isTerminalCommandStatus,
  useCommandFollow,
  useVpnConnect,
  useVpnDisconnect,
  useVpnPage,
} from '../../api/queries'
import { describeApiError } from '../../api/errors'
import { formatAbsolute, formatRelative } from '../../api/format'
import type { VpnRowState, VpnServerRow } from '../../api/types'
import { Badge, type BadgeTone } from '../../components/Badge'
import { Button } from '../../components/Button'
import { FreshnessBadge } from '../../components/FreshnessBadge'
import { InlineNotice } from '../../components/InlineNotice'
import { PageHeader } from '../../components/PageHeader'
import { EmptyBlock, ErrorBlock, LoadingBlock } from '../../components/AsyncState'
import { useToast } from '../../components/Toast'
import { cx } from '../../lib/cx'
import { VpnPolicyPanel } from './VpnPolicyPanel'
import styles from './VpnPage.module.css'

const rowStateLabels: Record<VpnRowState, string> = {
  Connected: '연결됨',
  Recent: '최근',
  Past: '과거',
  Never: '미사용',
}

const rowStateTones: Record<VpnRowState, BadgeTone> = {
  Connected: 'ok',
  Recent: 'accent',
  Past: 'neutral',
  Never: 'neutral',
}

function loadTone(load: number): BadgeTone {
  if (load <= 40) {
    return 'ok'
  }
  if (load <= 70) {
    return 'warn'
  }
  return 'danger'
}

export function VpnPage() {
  const { machineId } = useParams<{ machineId: string }>()
  const toast = useToast()
  const queryClient = useQueryClient()
  const [country, setCountry] = useState<string | undefined>(undefined)
  const [selectedCity, setSelectedCity] = useState<string | null>(null)
  const [selectedServer, setSelectedServer] = useState<VpnServerRow | null>(null)
  const [force, setForce] = useState(false)
  const [query, setQuery] = useState('')
  const [commandId, setCommandId] = useState<string | null>(null)
  const [commandKind, setCommandKind] = useState<'connect' | 'disconnect' | null>(null)

  const page = useVpnPage(machineId, country, undefined)
  const connect = useVpnConnect(machineId ?? '')
  const disconnect = useVpnDisconnect(machineId ?? '')
  const follow = useCommandFollow(commandId)

  const snapshot = page.data?.data
  const status = snapshot?.status
  const activeCountry = country ?? snapshot?.selectedCountry ?? ''
  const serverPage = snapshot?.servers
  const countryName =
    snapshot?.countries.find((item) => item.code === activeCountry)?.name ?? activeCountry
  const city = selectedCity ?? serverPage?.suggestedCity ?? ''
  const activeCommandId =
    commandId && !follow.expired && !isTerminalCommandStatus(follow.status)
      ? commandId
      : null
  const busy =
    Boolean(activeCommandId) ||
    Boolean(status?.busyOperation) ||
    Boolean(status?.busyCommandId) ||
    connect.isPending ||
    disconnect.isPending

  const groups = useMemo(() => {
    if (!serverPage) {
      return []
    }
    const needle = query.trim().toLowerCase()
    return serverPage.cities
      .map((group) => ({
        ...group,
        servers: needle
          ? group.servers.filter((server) =>
              [server.name, server.city ?? '', server.tierName, ...server.features]
                .join(' ')
                .toLowerCase()
                .includes(needle),
            )
          : group.servers,
      }))
      .filter((group) => group.servers.length > 0)
  }, [serverPage, query])

  const serverCount = useMemo(
    () => serverPage?.cities.reduce((total, group) => total + group.servers.length, 0) ?? 0,
    [serverPage],
  )

  useEffect(() => {
    if (!commandId || !isTerminalCommandStatus(follow.status)) {
      return
    }
    if (follow.status === 'Succeeded') {
      toast.notify(
        'success',
        commandKind === 'disconnect'
          ? 'VPN 연결을 해제했습니다.'
          : 'VPN 연결 명령이 완료되었습니다.',
      )
    } else {
      toast.notify(
        'error',
        follow.command?.errorMessage ?? 'VPN 명령이 실패했습니다.',
      )
    }
    if (machineId) {
      void queryClient.invalidateQueries({ queryKey: ['vpn-page', machineId] })
      void queryClient.invalidateQueries({ queryKey: ['machine', machineId] })
    }
  }, [commandId, commandKind, follow.command?.errorMessage, follow.status, machineId, queryClient, toast])

  if (!machineId) {
    return <EmptyBlock title="머신을 선택하세요" description="사이드바에서 머신을 선택합니다." />
  }

  if (page.isLoading) {
    return <LoadingBlock label="VPN 상태를 불러오는 중" />
  }

  if (page.isError) {
    return <ErrorBlock error={page.error} onRetry={() => void page.refetch()} />
  }

  if (!snapshot || !status) {
    return (
      <EmptyBlock
        title="VPN 상태를 사용할 수 없습니다"
        description="Client가 VPN 기능을 보고하지 않았습니다."
      />
    )
  }

  const changeCountry = (next: string) => {
    setCountry(next)
    setSelectedCity(null)
    setSelectedServer(null)
    setQuery('')
  }

  const runConnect = () => {
    const input = selectedServer
      ? { server: selectedServer.name, force }
      : { country: activeCountry, city: selectedCity ?? undefined, force }
    connect.mutate(input, {
      onSuccess: (result) => {
        setCommandKind('connect')
        setCommandId(result.data.id)
        if (result.data.kind === 'pending') {
          toast.notify('info', 'VPN 연결 명령을 접수했습니다.')
        }
      },
      onError: (error) => toast.notify('error', describeApiError(error).message),
    })
  }

  const runDisconnect = () => {
    disconnect.mutate(undefined, {
      onSuccess: (result) => {
        setCommandKind('disconnect')
        setCommandId(result.data.id)
        if (result.data.kind === 'pending') {
          toast.notify('info', 'VPN 연결 해제 명령을 접수했습니다.')
        }
      },
      onError: (error) => toast.notify('error', describeApiError(error).message),
    })
  }

  const connectLabel = selectedServer
    ? `${selectedServer.name} 연결`
    : `${countryName || activeCountry} 자동 연결`

  return (
    <div className={styles.page}>
      <PageHeader
        eyebrow="ProtonVPN"
        title="VPN"
        description="머신의 ProtonVPN 연결 상태를 확인하고 서버를 선택합니다."
        meta={
          <div className={styles.headerMeta}>
            <FreshnessBadge meta={page.data?.meta} />
            <Badge tone={status.tunnelUp ? 'ok' : 'neutral'}>
              {status.tunnelUp ? '터널 연결됨' : '터널 끊김'}
            </Badge>
          </div>
        }
        actions={
          <Button
            tone="secondary"
            size="sm"
            icon={<RefreshCw size={15} aria-hidden="true" />}
            loading={page.isFetching}
            onClick={() => void page.refetch()}
          >
            새로고침
          </Button>
        }
      />

      {!status.cliFound ? (
        <InlineNotice tone="danger" title="protonvpn CLI를 찾지 못했습니다">
          Client 설정의 VPN 경로를 확인하세요.
          {status.cliPath ? <code className={styles.inlineCode}>{status.cliPath}</code> : null}
        </InlineNotice>
      ) : null}

      {!status.helperAvailable && !status.usesProtonApp ? (
        <InlineNotice tone="warn" title="VPN Helper를 사용할 수 없습니다">
          연결 명령이 실패할 수 있습니다. Client 로그에서 helper 상태를 확인하세요.
        </InlineNotice>
      ) : null}

      {!status.signedIn ? (
        <InlineNotice tone="warn" title="ProtonVPN 로그인이 필요합니다">
          머신에서 ProtonVPN 계정으로 로그인한 뒤 새로고침하세요.
        </InlineNotice>
      ) : null}

      {status.protonGuiRunning && !status.usesProtonApp ? (
        <InlineNotice tone="info" title="ProtonVPN GUI가 실행 중입니다">
          CLI와 GUI가 동시에 연결을 제어하면 상태가 충돌할 수 있습니다.
        </InlineNotice>
      ) : null}

      {status.busyOperation || activeCommandId ? (
        <InlineNotice tone="info" title="VPN 명령 처리 중">
          {snapshot.actionText ??
            (activeCommandId
              ? `명령 ${activeCommandId}의 결과를 기다리는 중입니다.`
              : status.busyOperation)}
        </InlineNotice>
      ) : null}

      {follow.error ? (
        <InlineNotice tone="danger" title="명령 상태를 확인할 수 없습니다">
          {describeApiError(follow.error).message}
        </InlineNotice>
      ) : null}

      {follow.expired ? (
        <InlineNotice tone="warn" title="명령 결과 확인 시간이 초과되었습니다">
          VPN 상태를 새로고침해 실제 연결 상태를 확인하세요.
        </InlineNotice>
      ) : null}

      <VpnPolicyPanel machineId={machineId} />

      <div className={styles.topGrid}>
        <section className={cx(styles.statusPanel, status.tunnelUp && styles.statusPanelUp)}>
          <div className={styles.statusMain}>
            <span className={styles.statusIcon} aria-hidden="true">
              {status.tunnelUp ? <Shield size={28} /> : <ShieldOff size={28} />}
            </span>
            <div className={styles.statusCopy}>
              <p className={styles.statusEyebrow}>현재 연결</p>
              <p className={styles.statusTitle}>
                {status.tunnelUp ? (status.server ?? '연결됨') : '연결 안 됨'}
              </p>
              <p className={styles.statusSubtitle}>
                {status.tunnelUp
                  ? [status.country, status.city].filter(Boolean).join(' · ') || status.statusText
                  : status.statusText}
              </p>
            </div>
            <Badge tone={status.tunnelUp ? 'ok' : 'neutral'}>{status.statusText}</Badge>
          </div>

          <dl className={styles.detailGrid}>
            <div>
              <dt>국가</dt>
              <dd>{status.country ?? '—'}</dd>
            </div>
            <div>
              <dt>도시</dt>
              <dd>{status.city ?? '—'}</dd>
            </div>
            <div>
              <dt>출구 IP</dt>
              <dd>{status.exitIp ?? '—'}</dd>
            </div>
            <div>
              <dt>계정</dt>
              <dd>
                {status.username ??
                  (status.usesProtonApp && status.signedIn ? 'Proton VPN 앱' : '로그인 안 됨')}
              </dd>
            </div>
            <div>
              <dt>만료</dt>
              <dd title={formatAbsolute(status.expiresAt)}>
                {status.expiresAt ? formatRelative(status.expiresAt) : '—'}
              </dd>
            </div>
            <div>
              <dt>상태 갱신</dt>
              <dd title={formatAbsolute(status.asOf)}>{formatRelative(status.asOf)}</dd>
            </div>
          </dl>

          {status.detail ? <p className={styles.statusDetail}>{status.detail}</p> : null}
        </section>

        <section className={styles.controlPanel}>
          <div className={styles.panelHeading}>
            <div>
              <p className={styles.panelEyebrow}>Connection</p>
              <h2 className={styles.panelTitle}>연결 설정</h2>
            </div>
            <Power size={18} className={styles.panelIcon} aria-hidden="true" />
          </div>

          <label className={styles.field}>
            <span>국가</span>
            <select
              value={activeCountry}
              onChange={(event) => changeCountry(event.target.value)}
              disabled={snapshot.countries.length === 0}
            >
              {snapshot.countries.map((item) => (
                <option key={item.code} value={item.code}>
                  {item.name} ({item.code}) · {item.count}대
                </option>
              ))}
            </select>
          </label>

          <label className={styles.field}>
            <span>도시</span>
            <select
              value={selectedCity ?? ''}
              onChange={(event) => {
                setSelectedCity(event.target.value || null)
                setSelectedServer(null)
              }}
              disabled={!serverPage}
            >
              <option value="">
                자동{serverPage?.suggestedCity ? ` (${serverPage.suggestedCity} 권장)` : ''}
              </option>
              {serverPage?.cities.map((group) => (
                <option key={group.city} value={group.city}>
                  {group.city} · {group.count}대
                </option>
              ))}
            </select>
          </label>

          <label className={styles.checkbox}>
            <input
              type="checkbox"
              checked={force}
              onChange={(event) => setForce(event.target.checked)}
            />
            <span>현재 연결을 강제로 교체</span>
          </label>

          <div className={styles.selectionSummary}>
            {selectedServer ? (
              <>
                <MapPin size={14} aria-hidden="true" />
                <span>
                  선택한 서버 <strong>{selectedServer.name}</strong> · {selectedServer.city ?? '—'} ·
                  부하 {selectedServer.load}%
                </span>
                <button
                  type="button"
                  className={styles.textButton}
                  onClick={() => setSelectedServer(null)}
                >
                  자동 선택
                </button>
              </>
            ) : (
              <>
                <Globe2 size={14} aria-hidden="true" />
                <span>{city ? `${city}에서 자동으로 서버를 고릅니다.` : '서버를 자동으로 고릅니다.'}</span>
              </>
            )}
          </div>

          <div className={styles.controlActions}>
            <Button
              tone="primary"
              icon={<Power size={15} aria-hidden="true" />}
              loading={connect.isPending || (busy && commandKind === 'connect')}
              disabled={!status.cliFound || !status.signedIn || busy}
              onClick={runConnect}
            >
              {connectLabel}
            </Button>
            {status.tunnelUp ? (
              <Button
                tone="secondary"
                icon={<PowerOff size={15} aria-hidden="true" />}
                loading={disconnect.isPending || (busy && commandKind === 'disconnect')}
                disabled={busy}
                onClick={runDisconnect}
              >
                연결 해제
              </Button>
            ) : null}
          </div>
        </section>
      </div>

      <section className={styles.serversPanel}>
        <div className={styles.serversHeader}>
          <div>
            <p className={styles.panelEyebrow}>Servers</p>
            <h2 className={styles.panelTitle}>
              {countryName || '서버'} <span className={styles.count}>{serverCount}</span>
            </h2>
          </div>
          <div className={styles.serverTools}>
            {selectedServer ? (
              <button
                type="button"
                className={styles.textButton}
                onClick={() => setSelectedServer(null)}
              >
                자동 선택으로 초기화
              </button>
            ) : null}
            <label className={styles.search}>
              <Search size={14} aria-hidden="true" />
              <input
                value={query}
                onChange={(event) => setQuery(event.target.value)}
                placeholder="서버, 도시, 기능 검색"
                aria-label="서버 검색"
              />
            </label>
          </div>
        </div>

        {!serverPage ? (
          <EmptyBlock
            title="서버 목록이 없습니다"
            description="국가를 선택하거나 Client의 서버 캐시를 새로고침하세요."
          />
        ) : groups.length === 0 ? (
          <EmptyBlock title="검색 결과가 없습니다" description="다른 검색어를 입력하세요." />
        ) : (
          <div className={styles.cityGroups} role="list">
            {groups.map((group) => (
              <div key={group.city} className={styles.cityGroup} role="listitem">
                <div className={styles.cityHeader}>
                  <span className={styles.cityName}>{group.city}</span>
                  <span className={styles.cityCount}>{group.count}대</span>
                  {group.highlight ? <Badge tone="accent">권장</Badge> : null}
                </div>
                <div className={styles.serverRows}>
                  {group.servers.map((server) => {
                    const selected = selectedServer?.name === server.name
                    const loadClass =
                      server.load <= 40
                        ? styles.loadLow
                        : server.load <= 70
                          ? styles.loadMedium
                          : styles.loadHigh
                    return (
                      <button
                        key={server.name}
                        type="button"
                        className={cx(styles.serverRow, selected && styles.serverRowSelected)}
                        aria-pressed={selected}
                        onClick={() => setSelectedServer(selected ? null : server)}
                      >
                        <span className={styles.serverCheck} aria-hidden="true">
                          {selected ? <Check size={13} /> : null}
                        </span>
                        <span className={styles.serverIdentity}>
                          <span className={styles.serverName}>{server.name}</span>
                          <span className={styles.serverMeta}>
                            {server.city ?? '—'} · {server.tierName}
                            {server.features.length > 0 ? ` · ${server.features.join(', ')}` : ''}
                          </span>
                        </span>
                        <span className={styles.serverState}>
                          <Badge tone={rowStateTones[server.rowState]}>
                            {rowStateLabels[server.rowState]}
                          </Badge>
                          {server.lastConnectedAt ? (
                            <span
                              className={styles.lastConnected}
                              title={formatAbsolute(server.lastConnectedAt)}
                            >
                              {formatRelative(server.lastConnectedAt)}
                            </span>
                          ) : null}
                        </span>
                        <span className={styles.loadBlock}>
                          <span className={styles.loadHeader}>
                            <Activity size={12} aria-hidden="true" />
                            <span>{server.load}%</span>
                            <span className={styles.score}>점수 {server.score.toFixed(1)}</span>
                          </span>
                          <span className={styles.loadTrack}>
                            <span
                              className={cx(styles.loadBar, loadClass)}
                              style={{ width: `${Math.min(100, server.load)}%` }}
                            />
                          </span>
                        </span>
                        <Badge tone={loadTone(server.load)}>{server.tier}</Badge>
                      </button>
                    )
                  })}
                </div>
              </div>
            ))}
          </div>
        )}
      </section>
    </div>
  )
}

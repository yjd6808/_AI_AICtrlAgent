import { useEffect } from 'react'
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom'
import {
  Activity,
  ChevronDown,
  Menu,
  Settings,
  SquareTerminal,
  X,
} from 'lucide-react'
import { useInstances, useFeatures, useMachines, useMe } from '../api/queries'
import { buildSidebarSections } from './featureRoutes'
import { useUiStore } from '../flux/uiStore'
import { cx } from '../lib/cx'
import styles from './AppShell.module.css'

function currentMachineId(pathname: string): string | null {
  const match = pathname.match(/^\/m\/([^/]+)/)
  return match ? decodeURIComponent(match[1]) : null
}

const globalLinks = [
  { to: '/activity', label: '활동', icon: Activity },
  { to: '/commands', label: '명령', icon: SquareTerminal },
  { to: '/settings', label: '설정', icon: Settings },
]

export function AppShell() {
  const location = useLocation()
  const navigate = useNavigate()
  const me = useMe()
  const machines = useMachines()
  const sidebarOpen = useUiStore((state) => state.sidebarOpen)
  const setSidebarOpen = useUiStore((state) => state.setSidebarOpen)
  const lastMachineId = useUiStore((state) => state.lastMachineId)
  const setLastMachineId = useUiStore((state) => state.setLastMachineId)

  const pathMachineId = currentMachineId(location.pathname)
  const machineList = machines.data?.data ?? []
  const selectedMachine =
    machineList.find((machine) => machine.id.value === pathMachineId) ??
    machineList.find((machine) => machine.id.value === lastMachineId) ??
    machineList.find((machine) => machine.online) ??
    machineList[0] ??
    null
  const selectedMachineId = pathMachineId ?? selectedMachine?.id.value ?? null

  const instances = useInstances(selectedMachineId ?? undefined)
  const features = useFeatures(selectedMachineId ?? undefined)
  const sections = selectedMachineId
    ? buildSidebarSections(
        selectedMachineId,
        instances.data?.data ?? [],
        features.data?.data ?? [],
      )
    : []

  useEffect(() => {
    setSidebarOpen(false)
  }, [location.pathname, setSidebarOpen])

  useEffect(() => {
    if (selectedMachineId && selectedMachineId !== lastMachineId) {
      setLastMachineId(selectedMachineId)
    }
  }, [selectedMachineId, lastMachineId, setLastMachineId])

  const currentInstanceMatch = location.pathname.match(/\/agents\/([^/]+)/)
  const currentInstanceId = currentInstanceMatch
    ? decodeURIComponent(currentInstanceMatch[1])
    : null

  return (
    <div className={styles.shell}>
      <aside className={cx(styles.sidebar, sidebarOpen && styles.sidebarOpen)}>
        <div className={styles.brandRow}>
          <NavLink to="/" className={styles.brand} onClick={() => setSidebarOpen(false)}>
            <span className={styles.brandMark} aria-hidden="true">
              <SquareTerminal size={18} />
            </span>
            <span>
              <span className={styles.brandName}>AI CTRL</span>
              <span className={styles.brandSub}>Remote Console</span>
            </span>
          </NavLink>
          <button
            type="button"
            className={styles.closeButton}
            aria-label="메뉴 닫기"
            onClick={() => setSidebarOpen(false)}
          >
            <X size={19} aria-hidden="true" />
          </button>
        </div>

        <div className={styles.machinePicker}>
          <label className={styles.pickerLabel} htmlFor="machine-select">
            머신
          </label>
          <div className={styles.selectWrap}>
            <select
              id="machine-select"
              className={styles.select}
              value={selectedMachine?.id.value ?? ''}
              disabled={machines.isLoading || machineList.length === 0}
              onChange={(event) => {
                const next = machineList.find(
                  (machine) => machine.id.value === event.target.value,
                )
                if (next) {
                  navigate(`/m/${encodeURIComponent(next.id.value)}`)
                }
              }}
            >
              {machines.isLoading ? <option value="">머신 불러오는 중…</option> : null}
              {!machines.isLoading && machineList.length === 0 ? (
                <option value="">등록된 머신 없음</option>
              ) : null}
              {machineList.map((machine) => (
                <option key={machine.id.value} value={machine.id.value}>
                  {machine.hostname} {machine.online ? '· 온라인' : '· 오프라인'}
                </option>
              ))}
            </select>
            <ChevronDown size={15} className={styles.selectIcon} aria-hidden="true" />
          </div>
          {machines.isError ? (
            <p className={styles.pickerError}>머신 목록을 불러오지 못했습니다.</p>
          ) : null}
        </div>

        <nav className={styles.nav} aria-label="주 메뉴">
          <div className={styles.navGroup}>
            <p className={styles.navCaption}>콘솔</p>
            {globalLinks.map((link) => {
              const Icon = link.icon
              return (
                <NavLink
                  key={link.to}
                  to={link.to}
                  className={({ isActive }) =>
                    cx(styles.navLink, isActive && styles.navLinkActive)
                  }
                >
                  <Icon size={16} aria-hidden="true" />
                  {link.label}
                </NavLink>
              )
            })}
          </div>

          {selectedMachineId
            ? sections.map((section) => (
                <div className={styles.navGroup} key={section.section}>
                  <p className={styles.navCaption}>
                    {section.title}
                    {section.section === 'Agents' && section.items.length > 0 ? (
                      <span className={styles.count}>{section.items.length}</span>
                    ) : null}
                  </p>
                  {section.items.length === 0 ? (
                    <p className={styles.navEmpty}>표시할 인스턴스가 없습니다.</p>
                  ) : null}
                  {section.items.map((item) => (
                    <NavLink
                      key={item.key}
                      to={item.href}
                      className={({ isActive }) =>
                        cx(
                          styles.navLink,
                          isActive && styles.navLinkActive,
                          !item.available && styles.navLinkMuted,
                        )
                      }
                      title={item.unavailableReason ?? item.title}
                    >
                      <span className={styles.itemText}>{item.title}</span>
                      {item.badge ? (
                        <span className={styles.itemBadge}>{item.badge}</span>
                      ) : null}
                      {currentInstanceId && item.key === `agent:${currentInstanceId}` ? (
                        <span className={styles.activeMarker} aria-hidden="true" />
                      ) : null}
                    </NavLink>
                  ))}
                </div>
              ))
            : null}
        </nav>

        <div className={styles.sidebarFooter}>
          {instances.isError ? (
            <p className={styles.pickerError}>인스턴스 목록을 불러오지 못했습니다.</p>
          ) : null}
          <p className={styles.userEmail}>{me.data?.email ?? '사용자 정보 없음'}</p>
          <p className={styles.footerHint}>세션은 Google 로그인으로 보호됩니다.</p>
        </div>
      </aside>

      {sidebarOpen ? (
        <button
          type="button"
          className={styles.backdrop}
          aria-label="메뉴 닫기"
          onClick={() => setSidebarOpen(false)}
        />
      ) : null}

      <div className={styles.main}>
        <header className={styles.topbar}>
          <button
            type="button"
            className={styles.menuButton}
            aria-label="메뉴 열기"
            aria-expanded={sidebarOpen}
            onClick={() => setSidebarOpen(true)}
          >
            <Menu size={20} aria-hidden="true" />
          </button>
          <div className={styles.topbarTitle}>
            {selectedMachine ? (
              <>
                <span
                  className={cx(
                    styles.onlineDot,
                    selectedMachine.online ? styles.online : styles.offline,
                  )}
                  aria-hidden="true"
                />
                <span className={styles.hostname}>{selectedMachine.hostname}</span>
                <span className={styles.topbarMeta}>
                  {selectedMachine.clientVersion}
                </span>
              </>
            ) : (
              <span className={styles.topbarMeta}>AI Ctrl Agent</span>
            )}
          </div>
          {selectedMachine ? (
            <span className={styles.topbarStatus}>
              {selectedMachine.online ? '온라인' : '오프라인'}
            </span>
          ) : null}
        </header>

        <main className={styles.content}>
          <Outlet />
        </main>
      </div>
    </div>
  )
}

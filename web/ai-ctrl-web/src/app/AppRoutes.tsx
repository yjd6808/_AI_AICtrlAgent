import { lazy, Suspense, type ReactNode } from 'react'
import { Navigate, Route, Routes } from 'react-router-dom'
import { AppShell } from './AppShell'
import { AuthGuard } from './AuthGuard'
import { LoginPage } from './LoginPage'
import { HomeRedirect } from './HomeRedirect'
import { NotFoundPage } from './NotFoundPage'
import { LoadingBlock } from '../components/AsyncState'

const AgentsPage = lazy(() =>
  import('../features/agents/AgentsPage').then((module) => ({
    default: module.AgentsPage,
  })),
)
const SessionPage = lazy(() =>
  import('../features/session/SessionPage').then((module) => ({
    default: module.SessionPage,
  })),
)
const ActivityPage = lazy(() =>
  import('../features/activity/ActivityPage').then((module) => ({
    default: module.ActivityPage,
  })),
)
const CommandsPage = lazy(() =>
  import('../features/commands/CommandsPage').then((module) => ({
    default: module.CommandsPage,
  })),
)
const CommandDetailPage = lazy(() =>
  import('../features/commands/CommandDetailPage').then((module) => ({
    default: module.CommandDetailPage,
  })),
)
const LogsPage = lazy(() =>
  import('../features/logs/LogsPage').then((module) => ({
    default: module.LogsPage,
  })),
)
const SettingsPage = lazy(() =>
  import('../features/settings/SettingsPage').then((module) => ({
    default: module.SettingsPage,
  })),
)
const PlaceholderPage = lazy(() =>
  import('../features/placeholder/PlaceholderPage').then((module) => ({
    default: module.PlaceholderPage,
  })),
)

function LazyPage({ children }: { children: ReactNode }) {
  return (
    <Suspense fallback={<LoadingBlock label="화면을 불러오는 중…" />}>
      {children}
    </Suspense>
  )
}

export function AppRoutes() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route element={<AuthGuard />}>
        <Route element={<AppShell />}>
          <Route index element={<HomeRedirect />} />
          <Route
            path="activity"
            element={
              <LazyPage>
                <ActivityPage />
              </LazyPage>
            }
          />
          <Route
            path="commands"
            element={
              <LazyPage>
                <CommandsPage />
              </LazyPage>
            }
          />
          <Route
            path="commands/:commandId"
            element={
              <LazyPage>
                <CommandDetailPage />
              </LazyPage>
            }
          />
          <Route
            path="settings"
            element={
              <LazyPage>
                <SettingsPage />
              </LazyPage>
            }
          />
          <Route path="m/:machineId">
            <Route
              index
              element={
                <LazyPage>
                  <AgentsPage />
                </LazyPage>
              }
            />
            <Route
              path="agents/:instanceId"
              element={
                <LazyPage>
                  <AgentsPage />
                </LazyPage>
              }
            />
            <Route
              path="agents/:instanceId/sessions/:sessionId"
              element={
                <LazyPage>
                  <SessionPage />
                </LazyPage>
              }
            />
            <Route
              path="files/*"
              element={
                <LazyPage>
                  <PlaceholderPage kind="files" />
                </LazyPage>
              }
            />
            <Route
              path="vpn"
              element={
                <LazyPage>
                  <PlaceholderPage kind="vpn" />
                </LazyPage>
              }
            />
            <Route
              path="diagnostics"
              element={
                <LazyPage>
                  <PlaceholderPage kind="diagnostics" />
                </LazyPage>
              }
            />
            <Route
              path="logs"
              element={
                <LazyPage>
                  <LogsPage />
                </LazyPage>
              }
            />
            <Route
              path="logs/:name"
              element={
                <LazyPage>
                  <LogsPage />
                </LazyPage>
              }
            />
            <Route path="*" element={<NotFoundPage />} />
          </Route>
          <Route path="*" element={<NotFoundPage />} />
        </Route>
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}

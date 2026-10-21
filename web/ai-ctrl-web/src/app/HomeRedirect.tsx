import { useEffect } from 'react'
import { useMachines } from '../api/queries'
import { useUiStore } from '../flux/uiStore'
import { LoadingBlock, ErrorBlock } from '../components/AsyncState'
import { Navigate } from 'react-router-dom'

export function HomeRedirect() {
  const machines = useMachines()
  const lastMachineId = useUiStore((state) => state.lastMachineId)

  useEffect(() => {
    document.title = 'AI Ctrl Agent'
  }, [])

  if (machines.isLoading) {
    return <LoadingBlock label="머신 목록을 불러오는 중…" />
  }

  if (machines.isError) {
    return <ErrorBlock error={machines.error} onRetry={() => void machines.refetch()} />
  }

  const list = machines.data?.data ?? []
  const selected =
    list.find((machine) => machine.id.value === lastMachineId) ??
    list.find((machine) => machine.online) ??
    list[0]

  if (!selected) {
    return <Navigate to="/settings" replace />
  }

  return <Navigate to={`/m/${encodeURIComponent(selected.id.value)}`} replace />
}

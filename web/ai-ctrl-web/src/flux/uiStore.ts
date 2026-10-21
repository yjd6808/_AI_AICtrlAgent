import { create } from 'zustand'
import { persist } from 'zustand/middleware'

type UiState = {
  sidebarOpen: boolean
  lastMachineId: string | null
  setSidebarOpen: (open: boolean) => void
  toggleSidebar: () => void
  setLastMachineId: (machineId: string | null) => void
}

export const useUiStore = create<UiState>()(
  persist(
    (set) => ({
      sidebarOpen: false,
      lastMachineId: null,
      setSidebarOpen: (open) => set({ sidebarOpen: open }),
      toggleSidebar: () => set((state) => ({ sidebarOpen: !state.sidebarOpen })),
      setLastMachineId: (machineId) => set({ lastMachineId: machineId }),
    }),
    {
      name: 'ai-ctrl-ui',
      partialize: (state) => ({ lastMachineId: state.lastMachineId }),
    },
  ),
)

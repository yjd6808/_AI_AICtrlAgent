import { create } from 'zustand'
import type { ModelRef } from '../api/types'

export type PromptMode = 'prompt' | 'abort-and-prompt'
export type PendingPromptStatus = 'sending' | 'failed'

export type PendingPrompt = {
  id: string
  sessionKey: string
  text: string
  model: ModelRef | null
  mode: PromptMode
  status: PendingPromptStatus
  error: string | null
  commandId: string | null
  createdAt: number
}

type PendingPromptState = {
  items: Record<string, PendingPrompt>
  add: (prompt: PendingPrompt) => void
  setStatus: (id: string, status: PendingPromptStatus, error?: string | null) => void
  setCommandId: (id: string, commandId: string) => void
  remove: (id: string) => void
  clearSession: (sessionKey: string) => void
}

export const usePendingPromptStore = create<PendingPromptState>()((set) => ({
  items: {},
  add: (prompt) =>
    set((state) => ({
      items: { ...state.items, [prompt.id]: prompt },
    })),
  setStatus: (id, status, error = null) =>
    set((state) => {
      const existing = state.items[id]
      if (!existing) {
        return state
      }
      return {
        items: { ...state.items, [id]: { ...existing, status, error } },
      }
    }),
  setCommandId: (id, commandId) =>
    set((state) => {
      const existing = state.items[id]
      if (!existing) {
        return state
      }
      return {
        items: { ...state.items, [id]: { ...existing, commandId } },
      }
    }),
  remove: (id) =>
    set((state) => {
      if (!state.items[id]) {
        return state
      }
      const next = { ...state.items }
      delete next[id]
      return { items: next }
    }),
  clearSession: (sessionKey) =>
    set((state) => {
      const next: Record<string, PendingPrompt> = {}
      for (const [id, prompt] of Object.entries(state.items)) {
        if (prompt.sessionKey !== sessionKey) {
          next[id] = prompt
        }
      }
      return { items: next }
    }),
}))

export function pendingForSession(
  items: Record<string, PendingPrompt>,
  sessionKey: string,
): PendingPrompt[] {
  return Object.values(items)
    .filter((prompt) => prompt.sessionKey === sessionKey)
    .sort((left, right) => left.createdAt - right.createdAt)
}

export function sessionKeyOf(machineId: string, instanceId: string, sessionId: string): string {
  return `${machineId}/${instanceId}/${sessionId}`
}

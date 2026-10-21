import type { Id, SessionRef } from '../api/types'

export type RealtimeEvent =
  | { type: 'session.changed'; session: SessionRef }
  | { type: 'command.changed'; commandId: string }
  | { type: 'machine.changed'; machineId: string }
  | {
      type: 'message.changed'
      machineId: string
      instanceId: string
      sessionId: string
    }

export type RealtimeStatus = 'disconnected' | 'connecting' | 'connected' | 'reconnecting'

export interface RealtimeAdapter {
  readonly status: RealtimeStatus
  connect(): void
  disconnect(): void
  subscribe(listener: (event: RealtimeEvent) => void): () => void
  watchSession(session: SessionRef, leaseSeconds: number): void
  unwatchSession(session: SessionRef): void
}

export type RealtimeSessionRef = {
  machine: Id
  instance: Id
  sessionId: string
}

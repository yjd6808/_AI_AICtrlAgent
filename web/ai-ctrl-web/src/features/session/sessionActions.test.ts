import { describe, expect, it } from 'vitest'
import type { SessionDisplayState } from '../../api/types'
import { getSessionActions } from './sessionActions'

const allStates: SessionDisplayState[] = [
  'Running',
  'Retrying',
  'WaitingPermission',
  'Stalled',
  'QuotaBlocked',
  'Errored',
  'ForeignBusy',
  'Aborted',
  'Idle',
  'Unknown',
]

describe('getSessionActions', () => {
  it('only sends for quiescent or recoverable states', () => {
    for (const state of ['Idle', 'Errored', 'Aborted', 'QuotaBlocked'] as const) {
      expect(getSessionActions(state, true)).toEqual({
        send: true,
        abort: false,
        abortAndSend: false,
      })
    }
  })

  it('requires abort-and-send for busy states', () => {
    for (const state of [
      'Running',
      'Retrying',
      'Stalled',
      'WaitingPermission',
    ] as const) {
      expect(getSessionActions(state, true)).toEqual({
        send: false,
        abort: true,
        abortAndSend: true,
      })
    }
  })

  it('disables every action for ForeignBusy and Unknown', () => {
    for (const state of ['ForeignBusy', 'Unknown'] as const) {
      expect(getSessionActions(state, true)).toEqual({
        send: false,
        abort: false,
        abortAndSend: false,
      })
    }
  })

  it('disables every action for unmanaged instances', () => {
    for (const state of allStates) {
      expect(getSessionActions(state, false)).toEqual({
        send: false,
        abort: false,
        abortAndSend: false,
      })
    }
  })
})

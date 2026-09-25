import { strict as assert } from 'node:assert'
import test from 'node:test'
import type { CopyYesterdayCandidate, OrderResponse } from './api.ts'
import { defaultCopySelection } from './copyYesterday.ts'

function candidate(id: string, warning = false, alreadyCopiedOrderId: string | null = null,
  possibleExistingToday: OrderResponse[] = []): CopyYesterdayCandidate {
  return {
    sourceOrder: { id, zone: 'Centru', address: id, latitude: 47, longitude: 28,
      volume: 1, deliveryDate: '2026-09-24', status: 'Planned', sourceOrderId: null },
    deliveryStatus: warning ? 'Refused' : null, warnDeliveryOutcome: warning,
    alreadyCopiedOrderId, possibleExistingToday,
  }
}

test('refused outcomes are unselected; same-address candidates remain selected for review', () => {
  const possible = candidate('today').sourceOrder
  const candidates = [candidate('normal'), candidate('refused', true),
    candidate('possible-match', false, null, [possible]), candidate('copied', false, 'copy-id')]
  assert.deepEqual(defaultCopySelection(candidates), ['normal', 'possible-match'])
})

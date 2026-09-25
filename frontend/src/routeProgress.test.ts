import { strict as assert } from 'node:assert'
import test from 'node:test'
import { routeProgress } from './routeProgress.ts'

test('only final stop states count as finished, and refused is never delivered', () => {
  const stops = [
    { deliveryStatus: 'Pending' },
    { deliveryStatus: 'Departed' },
    { deliveryStatus: 'Arrived' },
    { deliveryStatus: 'Delivered' },
    { deliveryStatus: 'Refused' },
    { deliveryStatus: 'PartialReturn' },
  ] as const
  assert.deepEqual(routeProgress(stops), {
    finished: 3, total: 6, delivered: 1, refused: 1, partialReturn: 1,
  })
  assert.deepEqual(routeProgress([{ deliveryStatus: 'Refused' }]), {
    finished: 1, total: 1, delivered: 0, refused: 1, partialReturn: 0,
  })
  assert.deepEqual(routeProgress([]), {
    finished: 0, total: 0, delivered: 0, refused: 0, partialReturn: 0,
  })
})

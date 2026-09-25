import { strict as assert } from 'node:assert'
import test from 'node:test'
import type { OrderResponse, RoutePositionResponse, RouteResponse, RouteStopResponse } from './api.ts'
import { additionPreview, calculateEta, capacityUsage, cutoffSetting, prepareEta,
  stopMinutesSetting } from './routeIndicators.ts'

function stop(sequence: number, deliveryStatus: RouteStopResponse['deliveryStatus']): RouteStopResponse {
  return {
    id: String(sequence), orderId: String(sequence), sequence, deliveryStatus,
    estimatedArrival: null, zone: 'Test', address: `Stop ${sequence}`,
    latitude: 47 + sequence / 100, longitude: 28 + sequence / 100, volume: 1,
  }
}

const route: RouteResponse = {
  id: 'route', date: '2026-09-25', vehicle: { id: 'vehicle', registrationNumber: 'TEST', capacity: 5 },
  driver: { id: 'driver', fullName: 'Driver' }, totalVolume: 3.125,
  stops: [stop(3, 'Pending'), stop(1, 'Delivered'), stop(2, 'Refused')],
}
const order: OrderResponse = {
  id: 'order', zone: 'Test', address: 'New stop', latitude: 47, longitude: 28,
  volume: 1.875, deliveryDate: route.date, status: 'Confirmed',
}
const position: RoutePositionResponse = {
  routeId: route.id, latitude: 46.99, longitude: 28.99,
  reportedAt: '2026-09-25T09:00:00Z', receivedAt: '2026-09-25T09:00:00Z', source: 'Reported',
}
const now = Date.parse('2026-09-25T09:00:10Z')

test('capacity uses three-decimal volumes, including an exactly full vehicle', () => {
  assert.deepEqual(capacityUsage(route), { total: 3.125, capacity: 5, remaining: 1.875, percent: 62.5 })
  assert.deepEqual(additionPreview(route, order), { projected: 5, remaining: 0, exceeds: false })
  assert.deepEqual(additionPreview(route, { ...order, volume: 1.876 }),
    { projected: 5.001, remaining: -0.001, exceeds: true })
})

test('ETA visits only unfinished stops in Sequence order after the vehicle', () => {
  const preparation = prepareEta([
    stop(4, 'Arrived'), stop(3, 'PartialReturn'), stop(2, 'Pending'), stop(1, 'Delivered'),
  ], position, now)
  assert.deepEqual(preparation, {
    kind: 'ready', remainingStops: 2,
    points: [[46.99, 28.99], [47.02, 28.02], [47.04, 28.04]],
  })
})

test('ETA is unavailable without a recent valid position or valid remaining stop', () => {
  assert.deepEqual(prepareEta(route.stops, null, now),
    { kind: 'unavailable', reason: 'Poziția vehiculului lipsește.' })
  assert.equal(prepareEta(route.stops, position, now + 61_000).kind, 'unavailable')
  assert.equal(prepareEta([{ ...stop(3, 'Pending'), latitude: 100 }], position, now).kind, 'unavailable')
  assert.deepEqual(prepareEta([stop(1, 'Delivered'), stop(2, 'Refused')], null, now),
    { kind: 'complete' })
})

test('ETA adds OSRM driving time and configured stop time; deadline uses Chisinau delivery day', () => {
  // 09:00 UTC is 12:00 in Chisinau in September.
  const eta = calculateEta(Date.parse('2026-09-25T09:00:00Z'), 3600, 2, 10,
    '2026-09-25', '13:00')
  assert.equal(eta?.finish.toISOString(), '2026-09-25T10:20:00.000Z')
  assert.equal(eta?.drivingMinutes, 60)
  assert.equal(eta?.stationaryMinutes, 20)
  assert.equal(eta?.pastCutoff, true)
  assert.equal(calculateEta(Date.parse('2026-09-25T09:00:00Z'), 3600, 2, 10,
    '2026-09-25', '13:30')?.pastCutoff, false)
  assert.equal(calculateEta(Date.parse('2026-09-25T09:00:00Z'), 3600, 2, 10,
    '2026-09-25', '13:20')?.pastCutoff, false)
  assert.equal(calculateEta(Date.parse('2026-09-25T09:00:00Z'), 3600, 2, 10,
    '2026-09-25', null)?.pastCutoff, false)
  assert.equal(calculateEta(Date.parse('2026-09-25T21:00:00Z'), 3600, 1, 5,
    '2026-09-25', '23:59')?.pastCutoff, true)
  assert.equal(calculateEta(now, Number.NaN, 1, 5, route.date, null), null)
})

test('settings accept only bounded station time and a valid optional cutoff', () => {
  assert.equal(stopMinutesSetting(undefined), 5)
  assert.equal(stopMinutesSetting('0'), 0)
  assert.equal(stopMinutesSetting('241'), null)
  assert.equal(stopMinutesSetting('2.5'), null)
  assert.equal(cutoffSetting(undefined), null)
  assert.equal(cutoffSetting('18:00'), '18:00')
  assert.equal(cutoffSetting('25:00'), 'invalid')
})

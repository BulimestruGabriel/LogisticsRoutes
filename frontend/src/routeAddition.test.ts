import { strict as assert } from 'node:assert'
import test from 'node:test'
import type { OrderResponse, RouteResponse } from './api.ts'
import { routeAdditionCheck } from './routeAddition.ts'

const order: OrderResponse = {
  id: 'order', zone: 'Centru', address: 'Adresă test', latitude: 47, longitude: 28,
  volume: 1.875, deliveryDate: '2026-09-25', status: 'Confirmed', sourceOrderId: null,
}
const route: RouteResponse = {
  id: 'route', date: order.deliveryDate,
  vehicle: { id: 'vehicle', registrationNumber: 'TEST', capacity: 5 },
  driver: { id: 'driver', fullName: 'Șofer' }, totalVolume: 3.125,
  stops: [{ id: 'stop', sequence: 1, deliveryStatus: 'Pending', estimatedArrival: null,
    orderId: 'old-order', zone: 'CENTRU', address: 'Oprire existentă', latitude: 47, longitude: 28,
    volume: 3.125 }],
}

test('a confirmed order fits an unfinished route in the same day and zone', () => {
  assert.deepEqual(routeAdditionCheck(order, route), {
    selectable: true, accepted: true, reason: 'Ruta acceptă această comandă.',
  })
})

test('an over-capacity route remains selectable for preview but rejects a drop', () => {
  const check = routeAdditionCheck({ ...order, volume: 1.876 }, route)
  assert.equal(check.selectable, true)
  assert.equal(check.accepted, false)
  assert.match(check.reason, /Capacitatea/)
})

test('day, zone and started-stop conflicts cannot accept a drop', () => {
  const wrongDay = routeAdditionCheck(order, { ...route, date: '2026-09-26' })
  const wrongZone = routeAdditionCheck(order, { ...route,
    stops: [{ ...route.stops[0], zone: 'Botanica' }] })
  const started = routeAdditionCheck(order, { ...route,
    stops: [{ ...route.stops[0], deliveryStatus: 'Departed' }] })
  for (const check of [wrongDay, wrongZone, started]) {
    assert.equal(check.selectable, false)
    assert.equal(check.accepted, false)
  }
  assert.match(wrongDay.reason, /altă zi/)
  assert.match(wrongZone.reason, /Zona/)
  assert.match(started.reason, /început/)
})

test('only Confirmed orders and routes with existing stops are candidates', () => {
  assert.equal(routeAdditionCheck({ ...order, status: 'Planned' }, route).accepted, false)
  assert.equal(routeAdditionCheck(order, { ...route, stops: [] }).accepted, false)
})

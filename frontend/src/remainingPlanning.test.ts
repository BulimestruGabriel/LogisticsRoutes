import { strict as assert } from 'node:assert'
import test from 'node:test'
import type { DriverResponse, OrderResponse, RouteResponse, VehicleResponse } from './api.ts'
import { orderPlanability } from './remainingPlanning.ts'

const order: OrderResponse = {
  id: 'order', zone: 'Centru', address: 'Magazin', latitude: 47, longitude: 28.8,
  volume: 5, deliveryDate: '2026-10-05', status: 'Confirmed', sourceOrderId: null,
}
const vehicles: VehicleResponse[] = [
  { id: 'used-vehicle', registrationNumber: 'LIVE', capacity: 12.5 },
  { id: 'free-vehicle', registrationNumber: 'FREE', capacity: 12.5 },
]
const drivers: DriverResponse[] = [
  { id: 'used-driver', fullName: 'Used' }, { id: 'free-driver', fullName: 'Free' },
]
const liveRoute: RouteResponse = {
  id: 'live', date: order.deliveryDate,
  vehicle: vehicles[0], driver: drivers[0], totalVolume: 2,
  stops: [{ id: 'live-stop', sequence: 1, deliveryStatus: 'Pending', estimatedArrival: null,
    orderId: 'live-order', zone: 'live-test', address: 'Live', latitude: 47, longitude: 28.8, volume: 2 }],
}

test('a Centru order can use a free vehicle and driver while live-test route stays distinct', () => {
  const result = orderPlanability(order, [liveRoute], vehicles, drivers)
  assert.equal(result.actionable, true)
  assert.equal(result.newRoutePossible, true)
  assert.deepEqual(result.addableRouteIds, [])
})

test('an oversized confirmed order is blocked with a useful reason', () => {
  const result = orderPlanability({ ...order, volume: 13 }, [liveRoute], vehicles, drivers)
  assert.equal(result.actionable, false)
  assert.match(result.reason, /capacitatea maximă/)
})

test('a compatible existing route is actionable even with no free driver', () => {
  const centroRoute = { ...liveRoute, stops: liveRoute.stops.map(stop => ({ ...stop, zone: 'Centru' })) }
  const result = orderPlanability(order, [centroRoute], [vehicles[0]], [drivers[0]])
  assert.equal(result.actionable, true)
  assert.deepEqual(result.addableRouteIds, [centroRoute.id])
})

test('a new route needs both a free capable vehicle and a free driver', () => {
  assert.match(orderPlanability(order, [liveRoute], vehicles, [drivers[0]]).reason, /șofer liber/)
  assert.match(orderPlanability(order, [liveRoute], [vehicles[0]], drivers).reason, /vehicul liber/)
})

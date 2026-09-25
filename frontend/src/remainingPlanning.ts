import type { DriverResponse, OrderResponse, RouteResponse, VehicleResponse } from './api'
import { routeAdditionCheck } from './routeAddition.ts'
import { volumeFormat } from './format.ts'

export interface OrderPlanability { actionable: boolean; addableRouteIds: string[]; newRoutePossible: boolean; reason: string }

export function orderPlanability(order: OrderResponse, routes: RouteResponse[],
  vehicles: VehicleResponse[] | null, drivers: DriverResponse[] | null): OrderPlanability {
  if (order.status !== 'Confirmed') return { actionable: false, addableRouteIds: [], newRoutePossible: false, reason: 'Comanda nu este confirmată.' }
  const addableRouteIds = routes.filter(route => routeAdditionCheck(order, route).accepted).map(route => route.id)
  if (vehicles === null || drivers === null)
    return { actionable: addableRouteIds.length > 0, addableRouteIds, newRoutePossible: false,
      reason: addableRouteIds.length ? '' : 'Se verifică vehiculele și șoferii disponibili.' }

  const maximum = Math.max(0, ...vehicles.map(vehicle => vehicle.capacity))
  if (order.volume > maximum) return { actionable: false, addableRouteIds, newRoutePossible: false,
    reason: maximum === 0 ? 'Nu există vehicule în flotă.'
      : `Volumul ${volumeFormat.format(order.volume)} depășește capacitatea maximă ${volumeFormat.format(maximum)}. Corectează comanda.` }

  const usedVehicles = new Set(routes.map(route => route.vehicle.id))
  const usedDrivers = new Set(routes.map(route => route.driver.id))
  const freeVehicle = vehicles.some(vehicle => !usedVehicles.has(vehicle.id) && vehicle.capacity >= order.volume)
  const freeDriver = drivers.some(driver => !usedDrivers.has(driver.id))
  const newRoutePossible = freeVehicle && freeDriver
  const reason = addableRouteIds.length || newRoutePossible ? ''
    : !freeDriver ? 'Nu există șofer liber pentru o rută nouă; adaugă un șofer.'
      : 'Nu există vehicul liber cu suficientă capacitate; adaugă un vehicul potrivit.'
  return { actionable: addableRouteIds.length > 0 || newRoutePossible,
    addableRouteIds, newRoutePossible, reason }
}

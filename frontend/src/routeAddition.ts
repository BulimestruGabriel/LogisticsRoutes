import type { OrderResponse, RouteResponse } from './api'
import { additionPreview } from './routeIndicators.ts'

export interface RouteAdditionCheck {
  selectable: boolean
  accepted: boolean
  reason: string
}

// A client-side guide only. POST /api/routes/{routeId}/orders validates again in the API.
export function routeAdditionCheck(order: OrderResponse, route: RouteResponse): RouteAdditionCheck {
  if (order.status !== 'Confirmed')
    return { selectable: false, accepted: false, reason: 'Doar comenzile confirmate pot fi adăugate.' }
  if (route.date !== order.deliveryDate)
    return { selectable: false, accepted: false, reason: 'Ruta este pentru altă zi.' }
  if (route.stops.length === 0)
    return { selectable: false, accepted: false, reason: 'Ruta nu are opriri.' }
  if (route.stops.some(stop => stop.zone.toLocaleLowerCase('ro-RO') !== order.zone.toLocaleLowerCase('ro-RO')))
    return { selectable: false, accepted: false, reason: 'Zona rutei este diferită de zona comenzii.' }
  if (route.stops.some(stop => stop.deliveryStatus !== 'Pending'))
    return { selectable: false, accepted: false, reason: 'O oprire a rutei a început deja.' }

  const preview = additionPreview(route, order)
  if (!preview)
    return { selectable: true, accepted: false, reason: 'Capacitatea nu poate fi calculată.' }
  if (preview.exceeds)
    return { selectable: true, accepted: false, reason: 'Capacitatea vehiculului ar fi depășită.' }
  return { selectable: true, accepted: true, reason: 'Ruta acceptă această comandă.' }
}

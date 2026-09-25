import type { DeliveryStatus } from './api'

export interface RouteProgress {
  finished: number
  total: number
  delivered: number
  refused: number
  partialReturn: number
}

export function routeProgress(stops: ReadonlyArray<{ deliveryStatus: DeliveryStatus }>): RouteProgress {
  const progress: RouteProgress = {
    finished: 0,
    total: stops.length,
    delivered: 0,
    refused: 0,
    partialReturn: 0,
  }

  for (const stop of stops) {
    switch (stop.deliveryStatus) {
      case 'Delivered': progress.delivered++; break
      case 'Refused': progress.refused++; break
      case 'PartialReturn': progress.partialReturn++; break
    }
  }
  progress.finished = progress.delivered + progress.refused + progress.partialReturn
  return progress
}

import type { LatLngTuple } from 'leaflet'

export interface RoadRoute {
  positions: LatLngTuple[]
  distanceMeters: number | null
  durationSeconds: number | null
}

export function osrmRouteUrl(baseUrl: string, positions: LatLngTuple[]): string {
  const coordinates = positions.map(([latitude, longitude]) => `${longitude},${latitude}`).join(';')
  return `${baseUrl.replace(/\/+$/, '')}/route/v1/driving/${coordinates}?overview=full&geometries=geojson&steps=false`
}

function validCoordinate(value: unknown, minimum: number, maximum: number): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value >= minimum && value <= maximum
}

export function parseOsrmRoute(value: unknown): RoadRoute | null {
  if (!value || typeof value !== 'object') return null
  const response = value as { code?: unknown; routes?: unknown }
  if (response.code !== 'Ok' || !Array.isArray(response.routes)) return null
  const candidate = response.routes[0]
  if (!candidate || typeof candidate !== 'object') return null
  const route = candidate as { geometry?: unknown; distance?: unknown; duration?: unknown }
  const geometry = route.geometry as { type?: unknown; coordinates?: unknown } | null
  if (geometry?.type !== 'LineString' || !Array.isArray(geometry.coordinates) ||
      geometry.coordinates.length < 2) return null

  const positions: LatLngTuple[] = []
  for (const point of geometry.coordinates) {
    if (!Array.isArray(point) || point.length < 2 ||
        !validCoordinate(point[0], -180, 180) || !validCoordinate(point[1], -90, 90)) return null
    positions.push([point[1], point[0]])
  }
  const distanceMeters = validCoordinate(route.distance, 0, Number.MAX_VALUE) ? route.distance : null
  const durationSeconds = validCoordinate(route.duration, 0, Number.MAX_VALUE) ? route.duration : null
  return { positions, distanceMeters, durationSeconds }
}

export async function loadRoadRoute(baseUrl: string, positions: LatLngTuple[],
  signal?: AbortSignal, fetcher: typeof fetch = fetch): Promise<RoadRoute | null> {
  if (!baseUrl.trim() || positions.length < 2) return null
  try {
    const response = await fetcher(osrmRouteUrl(baseUrl.trim(), positions), { signal })
    if (!response.ok) return null
    return parseOsrmRoute(await response.json())
  } catch (error) {
    if (signal?.aborted) throw error
    return null
  }
}

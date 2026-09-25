import type { OrderResponse, RoutePositionResponse, RouteResponse, RouteStopResponse } from './api'

const volumeScale = 1000 // API volumes and capacities use three decimal places.
export const positionFreshnessMilliseconds = 60_000
export const etaTimeZone = 'Europe/Chisinau'

function volumeUnits(value: number): number | null {
  if (!Number.isFinite(value) || value < 0) return null
  const units = Math.round(value * volumeScale)
  return Number.isSafeInteger(units) ? units : null
}

export interface CapacityUsage {
  total: number
  capacity: number
  remaining: number
  percent: number
}

export function capacityUsage(route: RouteResponse): CapacityUsage | null {
  const total = volumeUnits(route.totalVolume)
  const capacity = volumeUnits(route.vehicle.capacity)
  if (total === null || capacity === null || capacity === 0) return null
  return {
    total: total / volumeScale,
    capacity: capacity / volumeScale,
    remaining: (capacity - total) / volumeScale,
    percent: total / capacity * 100,
  }
}

export function additionPreview(route: RouteResponse, order: OrderResponse) {
  const totalUnits = volumeUnits(route.totalVolume)
  const capacityUnits = volumeUnits(route.vehicle.capacity)
  const orderUnits = volumeUnits(order.volume)
  if (totalUnits === null || capacityUnits === null || capacityUnits === 0 || orderUnits === null) return null
  const projectedUnits = totalUnits + orderUnits
  if (!Number.isSafeInteger(projectedUnits)) return null
  return {
    projected: projectedUnits / volumeScale,
    remaining: (capacityUnits - projectedUnits) / volumeScale,
    exceeds: projectedUnits > capacityUnits,
  }
}

const finalStatuses = new Set(['Delivered', 'Refused', 'PartialReturn'])

function validCoordinates(latitude: number, longitude: number): boolean {
  return Number.isFinite(latitude) && latitude >= -90 && latitude <= 90 &&
    Number.isFinite(longitude) && longitude >= -180 && longitude <= 180
}

export type EtaPreparation =
  | { kind: 'complete' }
  | { kind: 'unavailable'; reason: string }
  | { kind: 'ready'; points: [number, number][]; remainingStops: number }

export function prepareEta(stops: readonly RouteStopResponse[], position: RoutePositionResponse | null,
  nowMilliseconds: number): EtaPreparation {
  const remaining = stops.filter(stop => !finalStatuses.has(stop.deliveryStatus))
    .sort((a, b) => a.sequence - b.sequence)
  if (remaining.length === 0) return { kind: 'complete' }
  if (!position) return { kind: 'unavailable', reason: 'Poziția vehiculului lipsește.' }
  const reportedAt = Date.parse(position.reportedAt)
  if (!Number.isFinite(reportedAt) || reportedAt > nowMilliseconds + positionFreshnessMilliseconds ||
    nowMilliseconds - reportedAt > positionFreshnessMilliseconds)
    return { kind: 'unavailable', reason: 'Poziția vehiculului este veche sau are o oră invalidă (peste 60 s).' }
  if (!validCoordinates(position.latitude, position.longitude))
    return { kind: 'unavailable', reason: 'Poziția vehiculului are coordonate invalide.' }
  if (remaining.some(stop => !validCoordinates(stop.latitude, stop.longitude)))
    return { kind: 'unavailable', reason: 'O oprire rămasă are coordonate invalide.' }
  return {
    kind: 'ready',
    points: [[position.latitude, position.longitude],
      ...remaining.map(stop => [stop.latitude, stop.longitude] as [number, number])],
    remainingStops: remaining.length,
  }
}

export function stopMinutesSetting(raw: string | undefined): number | null {
  if (raw === undefined || raw.trim() === '') return 5
  const value = Number(raw)
  return Number.isInteger(value) && value >= 0 && value <= 240 ? value : null
}

export function cutoffSetting(raw: string | undefined): string | null | 'invalid' {
  if (raw === undefined || raw.trim() === '') return null
  return /^([01]\d|2[0-3]):[0-5]\d$/.test(raw.trim()) ? raw.trim() : 'invalid'
}

function localMomentKey(date: Date): string {
  const parts = new Intl.DateTimeFormat('en-GB', {
    timeZone: etaTimeZone, year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23',
  }).formatToParts(date)
  const part = (type: string) => parts.find(item => item.type === type)!.value
  return `${part('year')}-${part('month')}-${part('day')}T${part('hour')}:${part('minute')}:${part('second')}`
}

export function calculateEta(nowMilliseconds: number, drivingSeconds: number, remainingStops: number,
  stopMinutes: number, routeDate: string, cutoff: string | null) {
  if (!Number.isFinite(drivingSeconds) || drivingSeconds < 0 || !Number.isInteger(remainingStops) ||
    remainingStops < 0 || !Number.isInteger(stopMinutes) || stopMinutes < 0) return null
  const finish = new Date(nowMilliseconds + (drivingSeconds + remainingStops * stopMinutes * 60) * 1000)
  if (!Number.isFinite(finish.getTime())) return null
  return {
    finish,
    drivingMinutes: drivingSeconds / 60,
    stationaryMinutes: remainingStops * stopMinutes,
    pastCutoff: cutoff !== null && localMomentKey(finish) > `${routeDate}T${cutoff}:00`,
  }
}

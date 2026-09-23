// These fields match the JSON returned by the ASP.NET Core DTOs in
// LogisticsRoutes.BusinessLayer/Models (camelCase serialization).
export type OrderStatus = 'New' | 'Confirmed' | 'Planned' | 'Delivered' | 'Cancelled'
export type DeliveryStatus = 'Pending' | 'Departed' | 'Arrived' | 'Delivered' | 'Refused' | 'PartialReturn'

export interface OrderResponse {
  id: string
  zone: string
  address: string
  latitude: number
  longitude: number
  volume: number
  deliveryDate: string
  status: OrderStatus
}

export interface CreateOrderRequest {
  zone: string
  address: string
  latitude: number
  longitude: number
  volume: number
  deliveryDate: string
}

export interface VehicleResponse {
  id: string
  registrationNumber: string
  capacity: number
}

export interface CreateVehicleRequest {
  registrationNumber: string
  capacity: number
}

export interface DriverResponse {
  id: string
  fullName: string
}

export interface CreateDriverRequest {
  fullName: string
}

export interface RouteStopResponse {
  id: string
  sequence: number
  deliveryStatus: DeliveryStatus
  estimatedArrival: string | null
  orderId: string
  zone: string
  address: string
  latitude: number
  longitude: number
  volume: number
}

export interface RouteResponse {
  id: string
  date: string
  vehicle: { id: string; registrationNumber: string }
  driver: { id: string; fullName: string }
  totalVolume: number
  stops: RouteStopResponse[]
}

export interface PlanRoutesRequest {
  day: string
}

export interface PlanRoutesResponse {
  day: string
  routes: Array<{
    id: string
    zone: string
    vehicleId: string
    driverId: string
    totalVolume: number
    stops: Array<{ orderId: string; sequence: number; address: string }>
  }>
}

export interface UpdateRouteStopStatusRequest {
  status: DeliveryStatus
}

export interface UpdateRouteStopStatusResponse {
  stopId: string
  deliveryStatus: DeliveryStatus
  orderStatus: OrderStatus
}

interface ProblemDetails {
  detail?: string
  title?: string
}

export class ApiError extends Error {
  constructor(public readonly status: number, message: string) {
    super(message)
    this.name = 'ApiError'
  }
}

async function request<T>(path: string, options: RequestInit = {}): Promise<T> {
  const response = await fetch(path, options)
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as ProblemDetails | null
    // Vite returns a plain-text 500 when its API proxy cannot reach the backend.
    if (!problem && response.status >= 500) throw new Error('API unavailable')
    throw new ApiError(response.status, problem?.detail || problem?.title || `HTTP ${response.status}`)
  }
  return response.json() as Promise<T>
}

export function getOrders(day: string, signal?: AbortSignal): Promise<OrderResponse[]> {
  return request(`/api/orders?day=${encodeURIComponent(day)}`, { signal })
}

export function getVehicles(signal?: AbortSignal): Promise<VehicleResponse[]> {
  return request('/api/vehicles', { signal })
}

export function createVehicle(body: CreateVehicleRequest): Promise<VehicleResponse> {
  return request('/api/vehicles', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
}

export function getDrivers(signal?: AbortSignal): Promise<DriverResponse[]> {
  return request('/api/drivers', { signal })
}

export function createDriver(body: CreateDriverRequest): Promise<DriverResponse> {
  return request('/api/drivers', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
}

export function createOrder(body: CreateOrderRequest): Promise<OrderResponse> {
  return request('/api/orders', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
}

export function confirmOrder(id: string): Promise<OrderResponse> {
  return request(`/api/orders/${encodeURIComponent(id)}/confirm`, { method: 'PATCH' })
}

export function getRoutes(day: string, signal?: AbortSignal): Promise<RouteResponse[]> {
  return request(`/api/routes?day=${encodeURIComponent(day)}`, { signal })
}

export function planRoutes(day: string): Promise<PlanRoutesResponse> {
  const body: PlanRoutesRequest = { day }
  return request('/api/routes/plan', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
}

export function updateRouteStopStatus(routeId: string, stopId: string,
  status: DeliveryStatus): Promise<UpdateRouteStopStatusResponse> {
  const body: UpdateRouteStopStatusRequest = { status }
  return request(`/api/routes/${encodeURIComponent(routeId)}/stops/${encodeURIComponent(stopId)}/status`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
}

export const dayRefreshMilliseconds = 30_000

interface RefreshClock {
  setInterval(callback: () => void, milliseconds: number): number
  clearInterval(id: number): void
  now(): Date
}

const browserClock: RefreshClock = {
  setInterval: (callback, milliseconds) => window.setInterval(callback, milliseconds),
  clearInterval: id => window.clearInterval(id),
  now: () => new Date(),
}

interface DayRefreshOptions<TOrder, TRoute> {
  day: string
  getOrders: (day: string, signal: AbortSignal) => Promise<TOrder[]>
  getRoutes: (day: string, signal: AbortSignal) => Promise<TRoute[]>
  onSuccess: (orders: TOrder[], routes: TRoute[], updatedAt: Date) => void
  onFailure: (error: unknown) => void
  clock?: RefreshClock
}

export function startDayRefresh<TOrder, TRoute>({
  day, getOrders, getRoutes, onSuccess, onFailure, clock = browserClock,
}: DayRefreshOptions<TOrder, TRoute>) {
  const controller = new AbortController()
  let inFlight = false

  async function refresh(): Promise<void> {
    if (controller.signal.aborted || inFlight) return
    inFlight = true
    try {
      // Wait for both requests even if one fails, so the next tick cannot overlap either request.
      const [orders, routes] = await Promise.allSettled([
        getOrders(day, controller.signal), getRoutes(day, controller.signal),
      ])
      if (controller.signal.aborted) return
      if (orders.status === 'rejected') {
        onFailure(orders.reason)
      } else if (routes.status === 'rejected') {
        onFailure(routes.reason)
      } else {
        onSuccess(orders.value, routes.value, clock.now())
      }
    } catch (error) {
      if (!controller.signal.aborted) onFailure(error)
    } finally {
      inFlight = false
    }
  }

  void refresh()
  const interval = clock.setInterval(() => { void refresh() }, dayRefreshMilliseconds)
  return {
    refresh,
    stop() {
      clock.clearInterval(interval)
      controller.abort()
    },
  }
}

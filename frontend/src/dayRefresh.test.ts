import { strict as assert } from 'node:assert'
import test from 'node:test'
import { dayRefreshMilliseconds, startDayRefresh } from './dayRefresh.ts'

function fakeClock() {
  let callback: (() => void) | null = null
  let stopped = false
  let interval = 0
  return {
    clock: {
      setInterval(next: () => void, milliseconds: number) {
        callback = next
        interval = milliseconds
        return 1
      },
      clearInterval(id: number) {
        assert.equal(id, 1)
        callback = null
        stopped = true
      },
      now: () => new Date('2026-09-25T12:00:00Z'),
    },
    tick: () => callback?.(),
    get interval() { return interval },
    get stopped() { return stopped },
  }
}

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>(done => { resolve = done })
  return { promise, resolve }
}

async function settle() {
  await new Promise<void>(resolve => setImmediate(resolve))
}

test('refreshes both lists every 30 seconds without overlapping slow requests', async () => {
  const timer = fakeClock()
  const firstOrders = deferred<string[]>()
  const calls: string[] = []
  const updates: string[] = []
  const refresh = startDayRefresh({
    day: '2026-09-25',
    getOrders: async (day, signal) => {
      assert.equal(signal.aborted, false)
      calls.push(`orders:${day}`)
      return calls.length === 1 ? firstOrders.promise : ['new order']
    },
    getRoutes: async day => { calls.push(`routes:${day}`); return ['route'] },
    onSuccess: orders => updates.push(orders[0]),
    onFailure: error => { throw error },
    clock: timer.clock,
  })

  assert.equal(timer.interval, dayRefreshMilliseconds)
  assert.deepEqual(calls, ['orders:2026-09-25', 'routes:2026-09-25'])
  timer.tick()
  await settle()
  assert.equal(calls.length, 2)

  firstOrders.resolve(['first order'])
  await settle()
  assert.deepEqual(updates, ['first order'])
  timer.tick()
  await settle()
  assert.deepEqual(updates, ['first order', 'new order'])
  assert.equal(calls.length, 4)
  refresh.stop()
  timer.tick()
  assert.equal(timer.stopped, true)
  assert.equal(calls.length, 4)
})

test('failed refresh keeps previous data; a later tick recovers', async () => {
  const timer = fakeClock()
  let attempt = 0
  let shownOrders: string[] = []
  let shownRoutes: string[] = []
  const updateTimes: Date[] = []
  let failures = 0
  const refresh = startDayRefresh({
    day: '2026-09-25',
    getOrders: async () => {
      attempt++
      if (attempt === 2) throw new Error('unavailable')
      return [`order ${attempt}`]
    },
    getRoutes: async () => ['route'],
    onSuccess: (orders, routes, at) => {
      shownOrders = orders
      shownRoutes = routes
      updateTimes.push(at)
    },
    onFailure: () => { failures++ },
    clock: timer.clock,
  })

  await settle()
  assert.deepEqual(shownOrders, ['order 1'])
  timer.tick()
  await settle()
  assert.equal(failures, 1)
  assert.deepEqual(shownOrders, ['order 1'])
  assert.deepEqual(shownRoutes, ['route'])
  assert.equal(updateTimes[0]?.toISOString(), '2026-09-25T12:00:00.000Z')
  timer.tick()
  await settle()
  assert.deepEqual(shownOrders, ['order 3'])
  refresh.stop()
})

test('stopping on day change aborts old requests and suppresses stale results', async () => {
  const oldTimer = fakeClock()
  const newTimer = fakeClock()
  const slowOrders = deferred<string[]>()
  const updates: string[] = []
  const oldSignals: AbortSignal[] = []
  const oldDay = startDayRefresh({
    day: '2026-09-25',
    getOrders: async (_day, signal) => { oldSignals.push(signal); return slowOrders.promise },
    getRoutes: async () => ['old route'],
    onSuccess: () => updates.push('old'),
    onFailure: () => updates.push('old failure'),
    clock: oldTimer.clock,
  })
  oldDay.stop()
  const newDay = startDayRefresh({
    day: '2026-09-26',
    getOrders: async () => ['new order'],
    getRoutes: async () => ['new route'],
    onSuccess: () => updates.push('new'),
    onFailure: () => updates.push('new failure'),
    clock: newTimer.clock,
  })
  slowOrders.resolve(['stale order'])
  await settle()
  assert.equal(oldSignals[0]?.aborted, true)
  assert.equal(oldTimer.stopped, true)
  assert.deepEqual(updates, ['new'])
  newDay.stop()
})

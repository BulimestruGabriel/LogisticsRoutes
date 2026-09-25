import { strict as assert } from 'node:assert'
import test from 'node:test'
import { loadRoadRoute, osrmRouteUrl, parseOsrmRoute } from './routing.ts'

const stops: [number, number][] = [[47.01, 28.01], [47.02, 28.02], [47.03, 28.03]]

test('OSRM URL keeps stop order and uses longitude,latitude', () => {
  assert.equal(osrmRouteUrl('https://routing.example/', stops),
    'https://routing.example/route/v1/driving/28.01,47.01;28.02,47.02;28.03,47.03?overview=full&geometries=geojson&steps=false')
})

test('GeoJSON line is interpreted as Leaflet latitude,longitude with metrics', () => {
  assert.deepEqual(parseOsrmRoute({
    code: 'Ok', routes: [{
      geometry: { type: 'LineString', coordinates: [[28.01, 47.01], [28.015, 47.016], [28.02, 47.02]] },
      distance: 2350, duration: 310,
    }],
  }), {
    positions: [[47.01, 28.01], [47.016, 28.015], [47.02, 28.02]],
    distanceMeters: 2350, durationSeconds: 310,
  })
})

test('missing metrics are optional but invalid geometry falls back', () => {
  assert.deepEqual(parseOsrmRoute({ code: 'Ok', routes: [{
    geometry: { type: 'LineString', coordinates: [[28, 47], [29, 48]] },
  }] }), { positions: [[47, 28], [48, 29]], distanceMeters: null, durationSeconds: null })
  assert.equal(parseOsrmRoute({ code: 'Ok', routes: [{
    geometry: { type: 'LineString', coordinates: [[28, 47], [400, 48]] },
  }] }), null)
  assert.equal(parseOsrmRoute({ code: 'NoRoute', routes: [] }), null)
})

test('service failure and NoRoute return fallback without hiding stops', async () => {
  const noRoute = (async () => new Response(JSON.stringify({ code: 'NoRoute' }),
    { headers: { 'Content-Type': 'application/json' } })) as typeof fetch
  const serverError = (async () => new Response('unavailable', { status: 503 })) as typeof fetch
  const unavailable = (async () => { throw new Error('network unavailable') }) as typeof fetch
  assert.equal(await loadRoadRoute('https://routing.example', stops, undefined, noRoute), null)
  assert.equal(await loadRoadRoute('https://routing.example', stops, undefined, serverError), null)
  assert.equal(await loadRoadRoute('https://routing.example', stops, undefined, unavailable), null)
})

test('one stop does not call the routing service', async () => {
  const unexpected = (async () => { throw new Error('unexpected request') }) as typeof fetch
  assert.equal(await loadRoadRoute('https://routing.example', [stops[0]], undefined, unexpected), null)
})

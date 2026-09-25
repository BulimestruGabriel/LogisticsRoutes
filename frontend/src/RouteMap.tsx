import { useEffect, useMemo, useState } from 'react'
import { divIcon, latLngBounds, type LatLngTuple } from 'leaflet'
import { MapContainer, Marker, Polyline, Popup, TileLayer, useMap } from 'react-leaflet'
import { getRoutePosition, type RoutePositionResponse, type RouteResponse, type RouteStopResponse } from './api'
import { deliveryLabels, numberFormat } from './format'
import { loadRoadRoute, type RoadRoute } from './routing'
import 'leaflet/dist/leaflet.css'

interface MappedStop {
  stop: RouteStopResponse
  position: LatLngTuple
}

function hasValidCoordinates(stop: RouteStopResponse): boolean {
  return typeof stop.latitude === 'number' && Number.isFinite(stop.latitude)
    && stop.latitude >= -90 && stop.latitude <= 90
    && typeof stop.longitude === 'number' && Number.isFinite(stop.longitude)
    && stop.longitude >= -180 && stop.longitude <= 180
}

function FitSelectedRoute({ positions }: { positions: LatLngTuple[] }) {
  const map = useMap()

  useEffect(() => {
    map.invalidateSize()
    if (positions.length === 1) {
      map.setView(positions[0], 14)
    } else if (positions.length > 1) {
      map.fitBounds(latLngBounds(positions), { padding: [36, 36], maxZoom: 15 })
    }
  }, [map, positions])

  return null
}

function numberedIcon(sequence: number) {
  const label = Number.isInteger(sequence) ? String(sequence) : '?'
  return divIcon({
    className: 'numbered-marker',
    html: `<span>${label}</span>`,
    iconSize: [32, 32],
    iconAnchor: [16, 16],
  })
}

function vehicleIcon(simulated: boolean) {
  return divIcon({
    className: `vehicle-marker${simulated ? ' vehicle-marker-simulated' : ''}`,
    html: `<span>${simulated ? 'SIM' : 'AUTO'}</span>`,
    iconSize: [44, 44],
    iconAnchor: [22, 22],
  })
}

const positionPollMilliseconds = 3000
const staleAfterMilliseconds = 60000

function formatMoment(value: string): string {
  return new Date(value).toLocaleString('ro-MD', { dateStyle: 'short', timeStyle: 'medium' })
}

export default function RouteMap({ route }: { route: RouteResponse }) {
  const { validStops, invalidStops } = useMemo(() => {
    const validStops: MappedStop[] = []
    const invalidStops: RouteStopResponse[] = []
    for (const stop of [...route.stops].sort((a, b) => a.sequence - b.sequence)) {
      if (hasValidCoordinates(stop)) {
        validStops.push({ stop, position: [stop.latitude, stop.longitude] })
      } else {
        invalidStops.push(stop)
      }
    }
    return { validStops, invalidStops }
  }, [route.stops])
  const positions = useMemo(() => validStops.map(item => item.position), [validStops])
  const routeKey = `${route.id}|${validStops.map(({ stop }) =>
    `${stop.id}:${stop.sequence}:${stop.latitude}:${stop.longitude}`).join('|')}`
  const routingBaseUrl = import.meta.env.VITE_OSRM_BASE_URL?.trim() ?? ''
  const [routing, setRouting] = useState<{ key: string; road: RoadRoute | null } | null>(null)
  const [tracking, setTracking] = useState<{
    routeId: string; position: RoutePositionResponse | null; error: boolean; loaded: boolean
  } | null>(null)
  const [now, setNow] = useState(() => Date.now())
  const currentTracking = tracking?.routeId === route.id ? tracking : null
  const vehicle = currentTracking?.position ?? null
  const vehiclePosition: LatLngTuple | null = vehicle ? [vehicle.latitude, vehicle.longitude] : null
  const stale = vehicle !== null && now - new Date(vehicle.reportedAt).getTime() > staleAfterMilliseconds
  const simulated = vehicle?.source === 'Simulated'
  const road = routing?.key === routeKey ? routing.road : null
  const routeLoading = positions.length > 1 && !!routingBaseUrl && routing?.key !== routeKey

  useEffect(() => {
    if (positions.length < 2) return
    const controller = new AbortController()
    loadRoadRoute(routingBaseUrl, positions, controller.signal)
      .then(result => {
        if (!controller.signal.aborted) setRouting({ key: routeKey, road: result })
      })
      .catch(() => {
        if (!controller.signal.aborted) setRouting({ key: routeKey, road: null })
      })
    return () => controller.abort()
  }, [routeKey, routingBaseUrl, positions])

  useEffect(() => {
    const controller = new AbortController()
    let polling = false
    setTracking({ routeId: route.id, position: null, error: false, loaded: false })
    const refresh = async () => {
      if (polling) return
      polling = true
      try {
        const position = await getRoutePosition(route.id, controller.signal)
        if (!controller.signal.aborted)
          setTracking({ routeId: route.id, position, error: false, loaded: true })
      } catch {
        if (!controller.signal.aborted)
          setTracking(current => ({ routeId: route.id,
            position: current?.routeId === route.id ? current.position : null,
            error: true, loaded: true }))
      } finally {
        polling = false
        if (!controller.signal.aborted) setNow(Date.now())
      }
    }
    void refresh()
    const interval = window.setInterval(() => { void refresh(); setNow(Date.now()) }, positionPollMilliseconds)
    return () => { controller.abort(); window.clearInterval(interval) }
  }, [route.id])

  const linePositions = road?.positions ?? positions
  const mapPositions = positions.length ? positions : vehiclePosition ? [vehiclePosition] : []

  return (
    <>
      <div className="vehicle-status" role="status" aria-live="polite">
        <span className={`vehicle-status-badge${!vehicle || stale || currentTracking?.error ? ' is-stale' : ''}`}>
          {!currentTracking?.loaded ? 'Se caută poziția…' : currentTracking.error
            ? 'Actualizare indisponibilă' : !vehicle ? 'Poziție lipsă'
              : stale ? 'Date vechi' : 'Poziție recentă'}
        </span>
        {vehicle ? <span>
          Ultima poziție primită: {formatMoment(vehicle.receivedAt)}. Raportată: {formatMoment(vehicle.reportedAt)}.
          {simulated && <strong> Poziție simulată — nu este GPS real.</strong>}
          {currentTracking?.error && ' Nu s-a putut actualiza poziția; se afișează ultima primită.'}
        </span> : <span>{!currentTracking?.loaded ? 'Se verifică ultima poziție raportată.' : currentTracking.error
          ? 'Poziția vehiculului nu poate fi citită momentan.'
          : 'Nu a fost raportată încă nicio poziție pentru această rută.'}</span>}
      </div>
      {invalidStops.length > 0 && <div className="map-warning" role="alert">
        <strong>{invalidStops.length === 1 ? 'O oprire are' : `${invalidStops.length} opriri au`} coordonate invalide.</strong>
        {' '}Nu {invalidStops.length === 1 ? 'poate fi afișată' : 'pot fi afișate'} pe hartă:
        {' '}{invalidStops.map(stop => `#${stop.sequence} ${stop.address}`).join('; ')}.
        {' '}Celelalte opriri rămân vizibile.
      </div>}

      {mapPositions.length === 0 ? <p className="map-unavailable">
        Harta nu poate fi afișată: ruta selectată nu are opriri sau poziție cu coordonate valide.
      </p> : <>
        <div className="map-frame">
          <MapContainer key={route.id} center={mapPositions[0]} zoom={13} scrollWheelZoom={false}
            className="route-map" aria-label="Harta opririlor rutei selectate">
            <TileLayer
              url="https://tile.openstreetmap.org/{z}/{x}/{y}.png"
              attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
            />
            <FitSelectedRoute positions={linePositions.length ? linePositions : mapPositions} />
            {positions.length > 1 && <Polyline key={`${routeKey}:${road ? 'road' : 'schematic'}`}
              positions={linePositions}
              pathOptions={{ color: '#527c32', weight: 3, opacity: 0.85,
                dashArray: road ? undefined : '7 7' }} />}
            {validStops.map(({ stop, position }) => <Marker key={stop.id} position={position} icon={numberedIcon(stop.sequence)}>
              <Popup>
                <div className="stop-popup">
                  <strong>Oprirea {stop.sequence}: {stop.address}</strong>
                  <span>Zonă: {stop.zone}</span>
                  <span>Volum: {numberFormat.format(stop.volume)}</span>
                  <span>Status: {deliveryLabels[stop.deliveryStatus] ?? stop.deliveryStatus}</span>
                </div>
              </Popup>
            </Marker>)}
            {vehiclePosition && <Marker position={vehiclePosition} icon={vehicleIcon(simulated)}
              zIndexOffset={1000} aria-label="Poziția vehiculului">
              <Popup><div className="stop-popup">
                <strong>Vehicul {route.vehicle.registrationNumber}</strong>
                <span>{simulated ? 'Poziție simulată — nu este GPS real.' : 'Poziție raportată.'}</span>
                <span>Raportată: {formatMoment(vehicle!.reportedAt)}</span>
                {stale && <span>Date vechi: peste 60 de secunde.</span>}
              </div></Popup>
            </Marker>}
          </MapContainer>
        </div>
        {positions.length > 1
          ? road
            ? <p className="map-legend"><span className="legend-line" aria-hidden="true" /><span>
              <strong>Traseu rutier estimat.</strong>
              {road.distanceMeters !== null && ` Distanță: ${numberFormat.format(road.distanceMeters / 1000)} km.`}
              {road.durationSeconds !== null && ` Durată estimată de condus: ${numberFormat.format(road.durationSeconds / 60)} min.`}
              {' '}Durata nu este o oră estimată de sosire.
            </span></p>
            : <p className="map-legend"><span className="legend-line schematic" aria-hidden="true" /><span>
              {routeLoading ? 'Se calculează traseul rutier… ' : 'Traseul rutier nu este disponibil. '}
              Linia schematică unește direct opririle afișate.
            </span></p>
          : <p className="map-legend">{positions.length === 1
            ? 'O singură oprire: nu există linie de legătură.'
            : 'Nu există opriri cu coordonate valide; se afișează doar poziția vehiculului.'}</p>}
      </>}
    </>
  )
}

import { useEffect, useMemo, useState } from 'react'
import { divIcon, latLngBounds, type LatLngTuple } from 'leaflet'
import { MapContainer, Marker, Polyline, Popup, TileLayer, useMap } from 'react-leaflet'
import type { RouteResponse, RouteStopResponse } from './api'
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

  const linePositions = road?.positions ?? positions

  return (
    <>
      {invalidStops.length > 0 && <div className="map-warning" role="alert">
        <strong>{invalidStops.length === 1 ? 'O oprire are' : `${invalidStops.length} opriri au`} coordonate invalide.</strong>
        {' '}Nu {invalidStops.length === 1 ? 'poate fi afișată' : 'pot fi afișate'} pe hartă:
        {' '}{invalidStops.map(stop => `#${stop.sequence} ${stop.address}`).join('; ')}.
        {' '}Celelalte opriri rămân vizibile.
      </div>}

      {validStops.length === 0 ? <p className="map-unavailable">
        Harta nu poate fi afișată: ruta selectată nu are opriri cu coordonate valide.
      </p> : <>
        <div className="map-frame">
          <MapContainer key={route.id} center={positions[0]} zoom={13} scrollWheelZoom={false}
            className="route-map" aria-label="Harta opririlor rutei selectate">
            <TileLayer
              url="https://tile.openstreetmap.org/{z}/{x}/{y}.png"
              attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
            />
            <FitSelectedRoute positions={linePositions} />
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
          : <p className="map-legend">O singură oprire: nu există linie de legătură.</p>}
      </>}
    </>
  )
}

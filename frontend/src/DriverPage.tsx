import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError, getRoute, reportRoutePosition, type RouteResponse } from './api'
import './driver.css'

type PermissionStateLabel = PermissionState | 'necunoscută'
type SignalState = 'oprit' | 'așteptare' | 'activ' | 'pierdut'

const permissionLabels: Record<PermissionStateLabel, string> = {
  granted: 'Permisă',
  prompt: 'Neacordată încă',
  denied: 'Refuzată',
  necunoscută: 'Necunoscută',
}

const signalLabels: Record<SignalState, string> = {
  oprit: 'Partajare oprită',
  așteptare: 'Se așteaptă poziția GPS',
  activ: 'Poziția se raportează',
  pierdut: 'Semnal GPS indisponibil',
}

function formatTime(value: string): string {
  return new Date(value).toLocaleString('ro-MD', { dateStyle: 'short', timeStyle: 'medium' })
}

export default function DriverPage({ routeId }: { routeId: string }) {
  const [route, setRoute] = useState<RouteResponse | null>(null)
  const [routeError, setRouteError] = useState<string | null>(null)
  const [token, setToken] = useState('')
  const [permission, setPermission] = useState<PermissionStateLabel>('necunoscută')
  const [sharing, setSharing] = useState(false)
  const [signal, setSignal] = useState<SignalState>('oprit')
  const [message, setMessage] = useState('Partajarea este oprită.')
  const [lastReportedAt, setLastReportedAt] = useState<string | null>(null)
  const watchId = useRef<number | null>(null)
  const request = useRef<AbortController | null>(null)
  const active = useRef(false)
  const sending = useRef(false)
  const lastFixTime = useRef<number | null>(null)
  const lastAttemptTime = useRef(0)

  const cancelSharing = useCallback(() => {
    active.current = false
    if (watchId.current !== null) navigator.geolocation.clearWatch(watchId.current)
    watchId.current = null
    request.current?.abort()
    request.current = null
    lastFixTime.current = null
    lastAttemptTime.current = 0
  }, [])

  const stopSharing = useCallback((reason = 'Partajarea este oprită.') => {
    cancelSharing()
    setSharing(false)
    setSignal('oprit')
    setMessage(reason)
  }, [cancelSharing])

  useEffect(() => {
    document.title = 'Poziția șoferului · LogisticsRoutes'
    let mounted = true
    getRoute(routeId).then(result => {
      if (mounted) setRoute(result)
    }).catch(() => {
      if (mounted) setRouteError('Ruta nu poate fi încărcată. Verifică legătura și reîncarcă pagina.')
    })
    return () => { mounted = false }
  }, [routeId])

  useEffect(() => {
    let mounted = true
    let permissionStatus: PermissionStatus | null = null
    if (navigator.permissions?.query) {
      navigator.permissions.query({ name: 'geolocation' }).then(status => {
        if (!mounted) return
        permissionStatus = status
        setPermission(status.state)
        status.onchange = () => setPermission(status.state)
      }).catch(() => { /* Unele browsere nu expun starea înainte de cerere. */ })
    }
    return () => {
      mounted = false
      if (permissionStatus) permissionStatus.onchange = null
    }
  }, [])

  useEffect(() => () => cancelSharing(), [cancelSharing])

  useEffect(() => {
    if (!sharing) return
    const timer = window.setInterval(() => {
      if (active.current && lastFixTime.current !== null && Date.now() - lastFixTime.current > 30_000) {
        setSignal('pierdut')
        setMessage('Nu a sosit nicio poziție GPS în ultimele 30 de secunde. Partajarea rămâne pornită.')
      }
    }, 5000)
    return () => window.clearInterval(timer)
  }, [sharing])

  function startSharing() {
    if (!route || !token.trim()) {
      setMessage('Introdu codul de acces primit pentru această rută.')
      return
    }
    if (!window.isSecureContext || !navigator.geolocation) {
      setMessage('Localizarea necesită o conexiune HTTPS de încredere și un browser cu GPS.')
      return
    }

    active.current = true
    lastFixTime.current = Date.now()
    setSharing(true)
    setSignal('așteptare')
    setMessage('Se solicită permisiunea și se așteaptă prima poziție.')

    try {
      watchId.current = navigator.geolocation.watchPosition(position => {
        if (!active.current) return
        setPermission('granted')
        lastFixTime.current = Date.now()
        setSignal('activ')
        if (sending.current || Date.now() - lastAttemptTime.current < 3000) return

        sending.current = true
        lastAttemptTime.current = Date.now()
        const controller = new AbortController()
        request.current = controller
        const reportedAt = new Date(Number.isFinite(position.timestamp) ? position.timestamp : Date.now()).toISOString()
        reportRoutePosition(routeId, token.trim(), position.coords.latitude, position.coords.longitude,
          reportedAt, controller.signal).then(saved => {
          if (!active.current) return
          setLastReportedAt(saved.reportedAt)
          setMessage('Poziția a fost trimisă către Dispecer.')
        }).catch(error => {
          if (!active.current || controller.signal.aborted) return
          if (error instanceof ApiError && (error.status === 401 || error.status === 403)) {
            stopSharing('Codul de acces este invalid, expirat sau aparține altei rute. Cere un cod nou.')
          } else {
            setMessage('Poziția nu a putut fi trimisă. Se va încerca din nou la următoarea poziție GPS.')
          }
        }).finally(() => {
          if (request.current === controller) request.current = null
          sending.current = false
        })
      }, error => {
        if (!active.current) return
        if (error.code === 1) {
          setPermission('denied')
          stopSharing('Permisiunea de localizare a fost refuzată. Activeaz-o din setările browserului.')
        } else {
          setSignal('pierdut')
          setMessage('Semnalul GPS lipsește sau a expirat așteptarea. Partajarea rămâne pornită.')
        }
      }, { enableHighAccuracy: true, maximumAge: 0, timeout: 15_000 })
    } catch {
      stopSharing('Localizarea nu a putut fi pornită pe acest dispozitiv.')
    }
  }

  return <main className="driver-page">
    <div className="driver-card">
      <p className="driver-kicker">LogisticsRoutes · Șofer</p>
      <h1>Partajarea poziției</h1>
      {routeError ? <p className="driver-error" role="alert">{routeError}</p>
        : route ? <div className="driver-route">
          <strong>{route.driver.fullName}</strong>
          <span>Ruta din {route.date} · {route.vehicle.registrationNumber} · {route.stops.length} opriri</span>
          <ol>{[...route.stops].sort((a, b) => a.sequence - b.sequence).map(stop =>
            <li key={stop.id}>{stop.address}</li>)}</ol>
        </div> : <p>Se încarcă ruta…</p>}

      <label className="driver-token" htmlFor="driver-access-token">Cod de acces pentru această rută
        <input id="driver-access-token" type="password" autoComplete="off" spellCheck={false}
          value={token} onChange={event => setToken(event.target.value)} disabled={sharing}
          placeholder="Lipește codul primit de la dispecer" />
      </label>

      <div className="driver-actions">
        <button type="button" className="driver-start" onClick={startSharing} disabled={sharing || !route || !!routeError}>
          Pornește partajarea poziției
        </button>
        <button type="button" className="driver-stop" onClick={() => stopSharing()} disabled={!sharing}>Oprește</button>
      </div>

      <div className="driver-state" role="status" aria-live="polite">
        <p><span>Permisiune localizare</span><strong>{permissionLabels[permission]}</strong></p>
        <p><span>Stare</span><strong>{signalLabels[signal]}</strong></p>
        <p><span>Ultima raportare</span><strong>{lastReportedAt ? formatTime(lastReportedAt) : 'Nicio raportare'}</strong></p>
        <p className="driver-message">{message}</p>
      </div>
      <p className="driver-note">Ține pagina deschisă cât timp partajezi poziția. Închiderea ei oprește raportările.</p>
    </div>
  </main>
}

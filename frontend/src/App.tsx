import { useEffect, useState } from 'react'
import {
  ApiError,
  getOrders,
  getRoutes,
  planRoutes,
  updateRouteStopStatus,
  type DeliveryStatus,
  type OrderResponse,
  type OrderStatus,
  type RouteResponse,
} from './api'
import { deliveryLabels, numberFormat } from './format'
import RouteMap from './RouteMap'

const orderLabels: Record<OrderStatus, string> = {
  New: 'Nouă',
  Confirmed: 'Confirmată',
  Planned: 'Planificată',
  Delivered: 'Livrată',
  Cancelled: 'Anulată',
}

const nextStopStatuses: Record<DeliveryStatus, DeliveryStatus[]> = {
  Pending: ['Departed'],
  Departed: ['Arrived'],
  Arrived: ['Delivered', 'Refused', 'PartialReturn'],
  Delivered: [],
  Refused: [],
  PartialReturn: [],
}

const stopActionLabels: Partial<Record<DeliveryStatus, string>> = {
  Departed: 'Marchează în drum',
  Arrived: 'Marchează la destinație',
  Delivered: 'Marchează livrată',
  Refused: 'Marchează refuzată',
  PartialReturn: 'Marchează retur parțial',
}

function todayLocal(): string {
  const now = new Date()
  const year = now.getFullYear()
  const month = String(now.getMonth() + 1).padStart(2, '0')
  const day = String(now.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

function readableDay(day: string): string {
  const [year, month, date] = day.split('-').map(Number)
  return new Intl.DateTimeFormat('ro-RO', {
    day: 'numeric', month: 'long', year: 'numeric',
  }).format(new Date(year, month - 1, date))
}

function errorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    return `API-ul a răspuns cu eroarea ${error.status}: ${error.message}`
  }
  return 'Conexiunea cu API-ul a eșuat. Verifică dacă API-ul rulează și încearcă din nou.'
}

function stopStatusErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 409) return `Tranziția a fost respinsă (409): ${error.message}`
    if (error.status === 404) return 'Ruta sau oprirea nu mai există (404). Reîncarcă datele.'
    if (error.status === 400) return `Status invalid (400): ${error.message}`
  }
  return errorMessage(error)
}

function App() {
  const [day, setDay] = useState(todayLocal)
  const [orders, setOrders] = useState<OrderResponse[]>([])
  const [routes, setRoutes] = useState<RouteResponse[]>([])
  const [selectedRouteId, setSelectedRouteId] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [planning, setPlanning] = useState(false)
  const [updatingStop, setUpdatingStop] = useState<{ stopId: string; status: DeliveryStatus } | null>(null)
  const [reload, setReload] = useState(0)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [statusError, setStatusError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    setLoading(true)
    setLoadError(null)
    setOrders([])
    setRoutes([])

    Promise.all([getOrders(day, controller.signal), getRoutes(day, controller.signal)])
      .then(([nextOrders, nextRoutes]) => {
        if (!controller.signal.aborted) {
          setOrders(nextOrders)
          setRoutes(nextRoutes)
        }
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setLoadError(errorMessage(error))
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })

    return () => controller.abort()
  }, [day, reload])

  async function handlePlan() {
    setPlanning(true)
    setActionError(null)
    setNotice(null)
    try {
      const result = await planRoutes(day)
      setNotice(result.routes.length === 0
        ? 'Nu există comenzi confirmate pentru această zi. Nu s-a creat nicio rută.'
        : `${result.routes.length} ${result.routes.length === 1 ? 'rută creată' : 'rute create'}. Se reîncarcă datele.`)
      setReload(value => value + 1)
    } catch (error) {
      if (error instanceof ApiError && error.status === 409) {
        setActionError(`Conflict (409): ${error.message} Verifică rutele și comenzile actualizate mai jos.`)
        setReload(value => value + 1)
      } else {
        setActionError(errorMessage(error))
      }
    } finally {
      setPlanning(false)
    }
  }

  async function handleStopStatus(routeId: string, stopId: string, status: DeliveryStatus) {
    if (updatingStop) return
    setUpdatingStop({ stopId, status })
    setStatusError(null)
    setActionError(null)
    setNotice(null)
    try {
      await updateRouteStopStatus(routeId, stopId, status)
      setSelectedRouteId(routeId)
      setNotice('Starea opririi a fost actualizată. Se reîncarcă rutele și comenzile.')
      setLoading(true)
      setReload(value => value + 1)
    } catch (error) {
      setStatusError(stopStatusErrorMessage(error))
      if (error instanceof ApiError && (error.status === 404 || error.status === 409)) {
        setLoading(true)
        setReload(value => value + 1)
      }
    } finally {
      setUpdatingStop(null)
    }
  }

  function handleDayChange(value: string) {
    if (!value) return
    setDay(value)
    setLoading(true)
    setOrders([])
    setRoutes([])
    setSelectedRouteId(null)
    setActionError(null)
    setStatusError(null)
    setNotice(null)
  }

  const confirmedCount = orders.filter(order => order.status === 'Confirmed').length
  const stopCount = routes.reduce((sum, route) => sum + route.stops.length, 0)
  const selectedRoute = routes.find(route => route.id === selectedRouteId) ?? routes[0]

  return (
    <div className="app-shell">
      <header className="topbar">
        <div className="brand"><span className="brand-mark" aria-hidden="true">L</span><span>LogisticsRoutes</span></div>
        <span className="workspace-label">Panou operațional</span>
      </header>

      <main className="main-content">
        <div className="page-heading">
          <div>
            <p className="eyebrow">PLANIFICARE LIVRĂRI</p>
            <h1>Dispecer</h1>
            <p className="subtitle">Comenzi și rute pentru ziua selectată, direct din sistem.</p>
          </div>
          <div className="heading-accent" aria-hidden="true"><span>↗</span></div>
        </div>

        <section className="toolbar" aria-label="Controale de planificare">
          <label className="date-field" htmlFor="delivery-day">
            <span>ZIUA LIVRĂRII</span>
            <input id="delivery-day" type="date" value={day}
              onChange={event => handleDayChange(event.target.value)} disabled={planning || updatingStop !== null} />
          </label>
          <div className="toolbar-meta">
            <span className="toolbar-date">{readableDay(day)}</span>
            <span className="toolbar-note">Comenzi și rute în această zi</span>
          </div>
          <button className="primary-button" type="button" onClick={handlePlan}
            disabled={loading || planning || updatingStop !== null || !!loadError}>
            <span aria-hidden="true">✦</span> {planning ? 'Se planifică…' : 'Planifică rutele'}
          </button>
        </section>

        {loadError && <div className="alert alert-error" role="alert">
          <strong>Nu am putut încărca datele.</strong> {loadError}
          <button type="button" onClick={() => setReload(value => value + 1)}>Reîncearcă</button>
        </div>}
        {actionError && <div className="alert alert-error" role="alert"><strong>Planificarea a eșuat.</strong> {actionError}</div>}
        {notice && <div className="alert alert-success" role="status">{notice}</div>}

        <div className="summary-grid" aria-label="Rezumatul zilei">
          <div className="summary-card"><span>COMENZI</span><strong>{loading ? '—' : orders.length}</strong><small>în ziua selectată</small></div>
          <div className="summary-card"><span>DE PLANIFICAT</span><strong>{loading ? '—' : confirmedCount}</strong><small>comenzi confirmate</small></div>
          <div className="summary-card"><span>RUTE</span><strong>{loading ? '—' : routes.length}</strong><small>{loading ? 'se încarcă' : `${stopCount} opriri în total`}</small></div>
        </div>

        <div className="content-grid">
          <section className="panel orders-panel" aria-labelledby="orders-title">
            <div className="panel-heading"><div><p className="section-kicker">01 / COMENZI</p><h2 id="orders-title">Comenzile zilei</h2></div><span className="count-pill">{loading ? '…' : orders.length}</span></div>
            {loading ? <p className="state-message" role="status">Se încarcă comenzile…</p>
              : loadError ? <p className="state-message">Comenzile nu sunt disponibile.</p>
              : orders.length === 0 ? <p className="state-message">Nu există comenzi pentru ziua selectată.</p>
              : <div className="table-scroll"><table>
                <thead><tr><th>Adresă / zonă</th><th>Volum</th><th>Status</th></tr></thead>
                <tbody>{orders.map(order => <tr key={order.id}>
                  <td><strong>{order.address}</strong><span className="secondary-line">{order.zone}</span></td>
                  <td className="number-cell">{numberFormat.format(order.volume)}</td>
                  <td><span className={`status status-${order.status.toLowerCase()}`}>{orderLabels[order.status] ?? order.status}</span></td>
                </tr>)}</tbody>
              </table></div>}
          </section>

          <section className="panel routes-panel" aria-labelledby="routes-title">
            <div className="panel-heading"><div><p className="section-kicker">02 / RUTE</p><h2 id="routes-title">Rutele zilei</h2></div><span className="count-pill">{loading ? '…' : routes.length}</span></div>
            {statusError && <div className="status-error" role="alert">{statusError}</div>}
            {loading ? <p className="state-message" role="status">Se încarcă rutele…</p>
              : loadError ? <p className="state-message">Rutele nu sunt disponibile.</p>
              : routes.length === 0 ? <div className="empty-routes"><div className="empty-icon" aria-hidden="true">⌁</div><strong>Nu există rute planificate</strong><p>Rutele pentru această zi vor apărea aici după planificare.</p></div>
              : <div className="route-list">{routes.map((route, index) => <article className={`route-card${selectedRoute?.id === route.id ? ' is-selected' : ''}`} key={route.id}>
                <div className="route-header"><div><span className="route-index">RUTA {String(index + 1).padStart(2, '0')}</span><h3>{route.stops[0]?.zone || 'Rută'}</h3></div><div className="route-header-actions"><span className="stop-count">{route.stops.length} {route.stops.length === 1 ? 'oprire' : 'opriri'}</span><button type="button" className="route-select-button" aria-pressed={selectedRoute?.id === route.id} onClick={() => { setSelectedRouteId(route.id); setStatusError(null) }} disabled={updatingStop !== null}>{selectedRoute?.id === route.id ? 'Pe hartă' : 'Vezi pe hartă'}</button></div></div>
                <div className="route-facts"><div><span>VEHICUL</span><strong>{route.vehicle.registrationNumber}</strong></div><div><span>ȘOFER</span><strong>{route.driver.fullName}</strong></div><div><span>VOLUM TOTAL</span><strong>{numberFormat.format(route.totalVolume)}</strong></div></div>
                <div className="stops"><p>OPRIRI ÎN ORDINE</p><ol>{[...route.stops].sort((a, b) => a.sequence - b.sequence).map(stop => <li key={stop.id}>
                  <span className="sequence">{String(stop.sequence).padStart(2, '0')}</span>
                  <span className="stop-detail"><strong>{stop.address}</strong><small>{stop.zone} · {numberFormat.format(stop.volume)} · {deliveryLabels[stop.deliveryStatus] ?? stop.deliveryStatus}</small>
                    {selectedRoute?.id === route.id && <span className="stop-actions">
                      {nextStopStatuses[stop.deliveryStatus]?.length
                        ? nextStopStatuses[stop.deliveryStatus].map(nextStatus => <button key={nextStatus} type="button"
                          onClick={() => handleStopStatus(route.id, stop.id, nextStatus)}
                          disabled={updatingStop !== null || loading || planning}>
                          {updatingStop?.stopId === stop.id && updatingStop.status === nextStatus
                            ? 'Se salvează…' : stopActionLabels[nextStatus]}
                        </button>)
                        : <span className="stop-final">Stare finală</span>}
                    </span>}
                  </span>
                </li>)}</ol></div>
              </article>)}</div>}
          </section>
        </div>

        <section className="panel map-panel" aria-labelledby="map-title">
          <div className="panel-heading"><div><p className="section-kicker">03 / HARTĂ</p><h2 id="map-title">Harta rutei {selectedRoute ? `· ${selectedRoute.stops[0]?.zone || 'selectate'}` : ''}</h2></div></div>
          {loading ? <p className="state-message" role="status">Se încarcă harta…</p>
            : loadError ? <p className="state-message">Harta nu este disponibilă până la încărcarea rutelor.</p>
            : !selectedRoute ? <p className="state-message">Nu există rute pentru această zi. Selectează altă zi sau planifică rutele pentru a vedea opririle pe hartă.</p>
            : <RouteMap route={selectedRoute} />}
        </section>
      </main>
    </div>
  )
}

export default App

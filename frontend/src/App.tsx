import { useEffect, useRef, useState } from 'react'
import {
  ApiError,
  addOrderToRoute,
  confirmOrder,
  createOrder,
  getOrders,
  getRoute,
  getRoutes,
  planRoutes,
  saveRouteStopOrder,
  updateRouteStopStatus,
  type DeliveryStatus,
  type CreateOrderRequest,
  type OrderResponse,
  type OrderStatus,
  type RouteResponse,
} from './api'
import { deliveryLabels, numberFormat } from './format'
import NewOrderForm from './NewOrderForm'
import ResourceManagement from './ResourceManagement'
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

function confirmErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 400) return 'Doar o comandă nouă poate fi confirmată. Lista se reîncarcă.'
    if (error.status === 404) return 'Comanda nu mai există. Lista se reîncarcă.'
    return `Confirmarea a eșuat (eroarea ${error.status}). Încearcă din nou.`
  }
  return 'Conexiunea cu API-ul a eșuat. Verifică dacă API-ul rulează și încearcă din nou.'
}

function addToRouteErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 409) return `${error.message} Listele se reîncarcă.`
    if (error.status === 404) return 'Ruta sau comanda nu mai există. Listele se reîncarcă.'
    if (error.status === 400) return 'Cererea este invalidă. Verifică ruta și comanda selectate.'
  }
  return errorMessage(error)
}

function compatibleRoutes(order: OrderResponse, routes: RouteResponse[]): RouteResponse[] {
  return routes.filter(route => route.date === order.deliveryDate && route.stops.length > 0 &&
    route.stops.every(stop => stop.deliveryStatus === 'Pending' &&
      stop.zone.toLocaleLowerCase('ro-RO') === order.zone.toLocaleLowerCase('ro-RO')) &&
    route.totalVolume + order.volume <= route.vehicle.capacity)
}

function unavailableRouteReason(order: OrderResponse, routes: RouteResponse[]): string {
  const sameDay = routes.filter(route => route.date === order.deliveryDate)
  if (sameDay.length === 0) return 'Nu există rute pentru ziua comenzii.'
  const sameZone = sameDay.filter(route => route.stops.length > 0 && route.stops.every(stop =>
    stop.zone.toLocaleLowerCase('ro-RO') === order.zone.toLocaleLowerCase('ro-RO')))
  if (sameZone.length === 0) return 'Nu există rută în zona comenzii.'
  const pending = sameZone.filter(route => route.stops.every(stop => stop.deliveryStatus === 'Pending'))
  if (pending.length === 0) return 'Opririle rutelor din această zonă au început deja.'
  return 'Capacitatea vehiculelor din această zonă este insuficientă.'
}

function App() {
  const [day, setDay] = useState(todayLocal)
  const [orders, setOrders] = useState<OrderResponse[]>([])
  const [routes, setRoutes] = useState<RouteResponse[]>([])
  const [selectedRouteId, setSelectedRouteId] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [planning, setPlanning] = useState(false)
  const [creatingOrder, setCreatingOrder] = useState(false)
  const [confirmingOrderId, setConfirmingOrderId] = useState<string | null>(null)
  const [addingOrderId, setAddingOrderId] = useState<string | null>(null)
  const [routeForOrder, setRouteForOrder] = useState<Record<string, string>>({})
  const [updatingStop, setUpdatingStop] = useState<{ stopId: string; status: DeliveryStatus } | null>(null)
  const mutationInFlight = useRef(false)
  const [reload, setReload] = useState(0)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [statusError, setStatusError] = useState<string | null>(null)
  const [confirmError, setConfirmError] = useState<string | null>(null)
  const [routeOrderError, setRouteOrderError] = useState<string | null>(null)
  const [stopOrderError, setStopOrderError] = useState<string | null>(null)
  const [draftOrder, setDraftOrder] = useState<{ routeId: string; stopIds: string[] } | null>(null)
  const [savingStopOrder, setSavingStopOrder] = useState(false)
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
    if (mutationInFlight.current) return
    mutationInFlight.current = true
    setPlanning(true)
    setActionError(null)
    setNotice(null)
    try {
      const result = await planRoutes(day)
      setNotice(result.routes.length === 0
        ? 'Nu există comenzi confirmate pentru această zi. Nu s-a creat nicio rută.'
        : `${result.routes.length} ${result.routes.length === 1 ? 'rută creată' : 'rute create'}.`)
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
      mutationInFlight.current = false
    }
  }

  async function handleStopStatus(routeId: string, stopId: string, status: DeliveryStatus) {
    if (mutationInFlight.current) return
    mutationInFlight.current = true
    setUpdatingStop({ stopId, status })
    setStatusError(null)
    setActionError(null)
    setNotice(null)
    try {
      await updateRouteStopStatus(routeId, stopId, status)
      setSelectedRouteId(routeId)
      setNotice('Starea opririi a fost actualizată.')
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
      mutationInFlight.current = false
    }
  }

  async function handleCreateOrder(request: CreateOrderRequest): Promise<OrderResponse> {
    if (mutationInFlight.current) throw new Error('A request is already in progress')
    mutationInFlight.current = true
    setCreatingOrder(true)
    setNotice(null)
    try {
      const created = await createOrder(request)
      setActionError(null)
      setStatusError(null)
      setConfirmError(null)
      if (created.deliveryDate === day) {
        setLoading(true)
        setReload(value => value + 1)
      } else {
        setDay(created.deliveryDate)
        setLoading(true)
        setOrders([])
        setRoutes([])
        setSelectedRouteId(null)
      }
      setNotice('Comanda a fost salvată. O poți confirma din lista comenzilor.')
      return created
    } finally {
      setCreatingOrder(false)
      mutationInFlight.current = false
    }
  }

  async function handleConfirmOrder(id: string) {
    if (mutationInFlight.current) return
    mutationInFlight.current = true
    setConfirmingOrderId(id)
    setConfirmError(null)
    setNotice(null)
    try {
      await confirmOrder(id)
      setNotice('Comanda a fost confirmată și este gata de planificare.')
      setLoading(true)
      setReload(value => value + 1)
    } catch (error) {
      setConfirmError(confirmErrorMessage(error))
      if (error instanceof ApiError && (error.status === 400 || error.status === 404)) {
        setLoading(true)
        setReload(value => value + 1)
      }
    } finally {
      setConfirmingOrderId(null)
      mutationInFlight.current = false
    }
  }

  async function handleAddToRoute(orderId: string, routeId: string) {
    if (mutationInFlight.current || !routeId) return
    mutationInFlight.current = true
    setAddingOrderId(orderId)
    setRouteOrderError(null)
    setNotice(null)
    try {
      await addOrderToRoute(routeId, orderId)
      setSelectedRouteId(current => current ?? selectedRoute?.id ?? routeId)
      setNotice('Comanda a fost adăugată. Ordinea opririlor a fost recalculată.')
      setLoading(true)
      setReload(value => value + 1)
    } catch (error) {
      setRouteOrderError(addToRouteErrorMessage(error))
      if (error instanceof ApiError && (error.status === 404 || error.status === 409)) {
        setLoading(true)
        setReload(value => value + 1)
      }
    } finally {
      setAddingOrderId(null)
      mutationInFlight.current = false
    }
  }

  function moveStop(routeId: string, fromId: string, toId: string) {
    setDraftOrder(current => {
      if (current?.routeId !== routeId) return current
      const from = current.stopIds.indexOf(fromId)
      const to = current.stopIds.indexOf(toId)
      if (from < 0 || to < 0 || from === to) return current
      const stopIds = [...current.stopIds]
      stopIds.splice(from, 1)
      stopIds.splice(to, 0, fromId)
      return { routeId, stopIds }
    })
  }

  async function handleSaveStopOrder() {
    if (!draftOrder || mutationInFlight.current) return
    const routeId = draftOrder.routeId
    mutationInFlight.current = true
    setSavingStopOrder(true)
    setStopOrderError(null)
    setNotice(null)
    try {
      await saveRouteStopOrder(routeId, draftOrder.stopIds)
      const refreshed = await getRoute(routeId)
      setRoutes(current => current.map(route => route.id === refreshed.id ? refreshed : route))
      setDraftOrder(null)
      setNotice('Ordinea opririlor a fost salvată.')
    } catch (error) {
      setDraftOrder(null)
      setStopOrderError(errorMessage(error))
      if (error instanceof ApiError && error.status === 409) {
        try {
          const refreshed = await getRoute(routeId)
          setRoutes(current => current.map(route => route.id === refreshed.id ? refreshed : route))
        } catch {
          // Keep the last loaded route and the original error visible.
        }
      }
    } finally {
      setSavingStopOrder(false)
      mutationInFlight.current = false
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
    setConfirmError(null)
    setRouteOrderError(null)
    setStopOrderError(null)
    setDraftOrder(null)
    setRouteForOrder({})
    setNotice(null)
  }

  const confirmedCount = orders.filter(order => order.status === 'Confirmed').length
  const stopCount = routes.reduce((sum, route) => sum + route.stops.length, 0)
  const selectedRoute = routes.find(route => route.id === selectedRouteId) ?? routes[0]
  const busy = planning || creatingOrder || confirmingOrderId !== null ||
    updatingStop !== null || addingOrderId !== null || savingStopOrder || draftOrder !== null

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
              onChange={event => handleDayChange(event.target.value)} disabled={busy} />
          </label>
          <div className="toolbar-meta">
            <span className="toolbar-date">{readableDay(day)}</span>
            <span className="toolbar-note">Comenzi și rute în această zi</span>
          </div>
          <button className="primary-button" type="button" onClick={handlePlan}
            disabled={loading || busy || !!loadError}>
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

        <ResourceManagement />

        <section className="panel new-order-panel" aria-labelledby="new-order-title">
          <div className="panel-heading"><div><p className="section-kicker">ADĂUGARE COMANDĂ</p><h2 id="new-order-title">Comandă nouă</h2></div></div>
          <NewOrderForm selectedDay={day} busy={busy} onSave={handleCreateOrder} />
        </section>

        <div className="content-grid">
          <section className="panel orders-panel" aria-labelledby="orders-title">
            <div className="panel-heading"><div><p className="section-kicker">01 / COMENZI</p><h2 id="orders-title">Comenzile zilei</h2></div><span className="count-pill">{loading ? '…' : orders.length}</span></div>
            {confirmError && <div className="status-error" role="alert">{confirmError}</div>}
            {routeOrderError && <div className="status-error" role="alert">{routeOrderError}</div>}
            {loading ? <p className="state-message" role="status">Se încarcă comenzile…</p>
              : loadError ? <p className="state-message">Comenzile nu sunt disponibile.</p>
              : orders.length === 0 ? <p className="state-message">Nu există comenzi pentru ziua selectată.</p>
              : <div className="table-scroll"><table>
                <thead><tr><th>Adresă / zonă</th><th>Volum</th><th>Status</th></tr></thead>
                <tbody>{orders.map(order => {
                  const availableRoutes = order.status === 'Confirmed' ? compatibleRoutes(order, routes) : []
                  const chosenRouteId = availableRoutes.some(route => route.id === routeForOrder[order.id])
                    ? routeForOrder[order.id] : availableRoutes[0]?.id ?? ''
                  return <tr key={order.id}>
                  <td><strong>{order.address}</strong><span className="secondary-line">{order.zone}</span></td>
                  <td className="number-cell">{numberFormat.format(order.volume)}</td>
                  <td className="order-status-cell"><span className={`status status-${order.status.toLowerCase()}`}>{orderLabels[order.status] ?? order.status}</span>
                    {order.status === 'New' && <button type="button" className="confirm-button"
                      onClick={() => handleConfirmOrder(order.id)} disabled={busy || loading}
                      aria-label={`Confirmă comanda ${order.address}`}>
                      {confirmingOrderId === order.id ? 'Se confirmă…' : 'Confirmă'}
                    </button>}
                    {order.status === 'Confirmed' && (availableRoutes.length > 0
                      ? <div className="add-to-route-controls">
                        <select aria-label={`Rută pentru comanda ${order.address}`} value={chosenRouteId}
                          onChange={event => { setRouteForOrder(current => ({ ...current, [order.id]: event.target.value })); setRouteOrderError(null) }}
                          disabled={busy || loading}>
                          {availableRoutes.map(route => <option value={route.id} key={route.id}>
                            Ruta {routes.indexOf(route) + 1} · {route.vehicle.registrationNumber} · liber {numberFormat.format(route.vehicle.capacity - route.totalVolume)}
                          </option>)}
                        </select>
                        <button type="button" className="confirm-button" onClick={() => handleAddToRoute(order.id, chosenRouteId)}
                          disabled={busy || loading}>
                          {addingOrderId === order.id ? 'Se adaugă…' : 'Adaugă la rută'}
                        </button>
                      </div>
                      : <span className="route-unavailable">{unavailableRouteReason(order, routes)}</span>)}
                  </td>
                </tr>})}</tbody>
              </table></div>}
          </section>

          <section className="panel routes-panel" aria-labelledby="routes-title">
            <div className="panel-heading"><div><p className="section-kicker">02 / RUTE</p><h2 id="routes-title">Rutele zilei</h2></div><span className="count-pill">{loading ? '…' : routes.length}</span></div>
            {statusError && <div className="status-error" role="alert">{statusError}</div>}
            {stopOrderError && <div className="status-error" role="alert">{stopOrderError}</div>}
            {loading ? <p className="state-message" role="status">Se încarcă rutele…</p>
              : loadError ? <p className="state-message">Rutele nu sunt disponibile.</p>
              : routes.length === 0 ? <div className="empty-routes"><div className="empty-icon" aria-hidden="true">⌁</div><strong>Nu există rute planificate</strong><p>Rutele pentru această zi vor apărea aici după planificare.</p></div>
              : <div className="route-list">{routes.map((route, index) => {
                const editing = draftOrder?.routeId === route.id
                const canReorder = route.stops.length > 1 && route.stops.every(stop => stop.deliveryStatus === 'Pending')
                const savedStops = [...route.stops].sort((a, b) => a.sequence - b.sequence)
                const orderedStops = editing
                  ? draftOrder.stopIds.map(id => route.stops.find(stop => stop.id === id)!).filter(Boolean)
                  : savedStops
                const changed = editing && orderedStops.some((stop, position) => stop.id !== savedStops[position]?.id)
                return <article className={`route-card${selectedRoute?.id === route.id ? ' is-selected' : ''}`} key={route.id}>
                <div className="route-header"><div><span className="route-index">RUTA {String(index + 1).padStart(2, '0')}</span><h3>{route.stops[0]?.zone || 'Rută'}</h3></div><div className="route-header-actions"><span className="stop-count">{route.stops.length} {route.stops.length === 1 ? 'oprire' : 'opriri'}</span><button type="button" className="route-select-button" aria-pressed={selectedRoute?.id === route.id} onClick={() => { setSelectedRouteId(route.id); setStatusError(null) }} disabled={busy}>{selectedRoute?.id === route.id ? 'Pe hartă' : 'Vezi pe hartă'}</button></div></div>
                <div className="route-facts"><div><span>VEHICUL</span><strong>{route.vehicle.registrationNumber}</strong></div><div><span>ȘOFER</span><strong>{route.driver.fullName}</strong></div><div><span>VOLUM TOTAL</span><strong>{numberFormat.format(route.totalVolume)}</strong></div></div>
                <div className="stops"><div className="stop-order-heading"><p>{editing ? 'ORDINE PROPUSĂ' : 'OPRIRI ÎN ORDINE'}</p>
                  {selectedRoute?.id === route.id && !editing && canReorder &&
                    <button type="button" className="route-select-button" disabled={busy || loading}
                      onClick={() => { setDraftOrder({ routeId: route.id, stopIds: orderedStops.map(stop => stop.id) }); setStopOrderError(null) }}>
                      Reordonează opririle
                    </button>}
                </div>
                {selectedRoute?.id === route.id && !editing && !canReorder && route.stops.some(stop => stop.deliveryStatus !== 'Pending') &&
                  <p className="reorder-note">Ordinea nu mai poate fi schimbată după începerea unei opriri.</p>}
                <ol>{orderedStops.map((stop, position) => <li key={stop.id}
                  className={editing ? 'reorder-stop' : undefined}
                  draggable={editing && !savingStopOrder}
                  onDragStart={editing ? event => { event.dataTransfer.setData('text/plain', stop.id); event.dataTransfer.effectAllowed = 'move' } : undefined}
                  onDragOver={editing ? event => { event.preventDefault(); event.dataTransfer.dropEffect = 'move' } : undefined}
                  onDrop={editing ? event => { event.preventDefault(); moveStop(route.id, event.dataTransfer.getData('text/plain'), stop.id) } : undefined}>
                  <span className="sequence">{String(position + 1).padStart(2, '0')}</span>
                  <span className="stop-detail"><strong>{stop.address}</strong><small>{stop.zone} · {numberFormat.format(stop.volume)} · {deliveryLabels[stop.deliveryStatus] ?? stop.deliveryStatus}</small>
                    {editing && <span className="stop-actions reorder-actions">
                      <button type="button" disabled={position === 0 || savingStopOrder}
                        onClick={() => moveStop(route.id, stop.id, orderedStops[position - 1].id)}
                        aria-label={`Mută oprirea ${stop.address} sus`}>Sus</button>
                      <button type="button" disabled={position === orderedStops.length - 1 || savingStopOrder}
                        onClick={() => moveStop(route.id, stop.id, orderedStops[position + 1].id)}
                        aria-label={`Mută oprirea ${stop.address} jos`}>Jos</button>
                    </span>}
                    {selectedRoute?.id === route.id && !editing && <span className="stop-actions">
                      {nextStopStatuses[stop.deliveryStatus]?.length
                        ? nextStopStatuses[stop.deliveryStatus].map(nextStatus => <button key={nextStatus} type="button"
                          onClick={() => handleStopStatus(route.id, stop.id, nextStatus)}
                          disabled={busy || loading}>
                          {updatingStop?.stopId === stop.id && updatingStop.status === nextStatus
                            ? 'Se salvează…' : stopActionLabels[nextStatus]}
                        </button>)
                        : <span className="stop-final">Stare finală</span>}
                    </span>}
                  </span>
                </li>)}</ol>
                {editing && <div className="reorder-footer">
                  <span>Trage opririle sau folosește butoanele Sus/Jos.</span>
                  <div><button type="button" className="route-select-button" disabled={savingStopOrder}
                    onClick={() => setDraftOrder(null)}>Anulează</button>
                    <button type="button" className="confirm-button" disabled={!changed || savingStopOrder}
                      onClick={handleSaveStopOrder}>{savingStopOrder ? 'Se salvează…' : 'Salvează ordinea'}</button></div>
                </div>}</div>
              </article>})}</div>}
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

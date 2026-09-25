import { useEffect, useRef, useState, type DragEvent } from 'react'
import {
  ApiError,
  addOrderToRoute,
  confirmOrder,
  correctConfirmedVolume,
  createOrder,
  getOrders,
  getVehicles,
  getDrivers,
  getRoute,
  getRoutes,
  moveRouteStop,
  planRemaining,
  saveRouteStopOrder,
  updateRouteStopStatus,
  updateOrderVolume,
  type DeliveryStatus,
  type CreateOrderRequest,
  type OrderResponse,
  type OrderStatus,
  type VehicleResponse,
  type DriverResponse,
  type RouteResponse,
} from './api'
import { deliveryLabels, volumeFormat } from './format'
import { startDayRefresh } from './dayRefresh'
import NewOrderForm from './NewOrderForm'
import NewOrderVolume from './NewOrderVolume'
import ConfirmedVolumeCorrection from './ConfirmedVolumeCorrection'
import CopyYesterdayPanel from './CopyYesterdayPanel'
import ResourceManagement from './ResourceManagement'
import RouteMap from './RouteMap'
import { routeAdditionCheck } from './routeAddition'
import { orderPlanability } from './remainingPlanning'
import { additionPreview, capacityUsage } from './routeIndicators'
import { routeProgress } from './routeProgress'

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
    if (error.status === 409) return `Confirmarea a fost refuzată (409): ${error.message}`
    if (error.status === 400) return 'Doar o comandă nouă poate fi confirmată. Lista se reîncarcă.'
    if (error.status === 404) return 'Comanda nu mai există. Lista se reîncarcă.'
    return `Confirmarea a eșuat (eroarea ${error.status}). Încearcă din nou.`
  }
  return 'Conexiunea cu API-ul a eșuat. Verifică dacă API-ul rulează și încearcă din nou.'
}

function addToRouteErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 409) return `Conflict (409): ${error.message} Listele se reîncarcă.`
    if (error.status === 404) return 'Ruta sau comanda nu mai există. Listele se reîncarcă.'
    if (error.status === 400) return `Cerere invalidă (400): ${error.message}`
  }
  return errorMessage(error)
}

function moveStopErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 409) return `Mutarea a fost respinsă: ${error.message}`
    if (error.status === 404) return 'Ruta sau oprirea nu mai există în datele actuale.'
    if (error.status === 400) return `Cererea de mutare este invalidă: ${error.message}`
  }
  return errorMessage(error)
}

function compatibleRoutes(order: OrderResponse, routes: RouteResponse[]): RouteResponse[] {
  return routes.filter(route => routeAdditionCheck(order, route).selectable)
}

function unavailableRouteReason(order: OrderResponse, routes: RouteResponse[]): string {
  const sameDay = routes.filter(route => route.date === order.deliveryDate)
  if (sameDay.length === 0) return 'Nu există rute pentru ziua comenzii.'
  const sameZone = sameDay.filter(route => route.stops.length > 0 && route.stops.every(stop =>
    stop.zone.toLocaleLowerCase('ro-RO') === order.zone.toLocaleLowerCase('ro-RO')))
  if (sameZone.length === 0) return 'Nu există rută în zona comenzii.'
  const pending = sameZone.filter(route => route.stops.every(stop => stop.deliveryStatus === 'Pending'))
  if (pending.length === 0) return 'Opririle rutelor din această zonă au început deja.'
  return 'Nu există rută eligibilă pentru această comandă.'
}

function App() {
  const [day, setDay] = useState(todayLocal)
  const [orders, setOrders] = useState<OrderResponse[]>([])
  const [routes, setRoutes] = useState<RouteResponse[]>([])
  const [vehicles, setVehicles] = useState<VehicleResponse[] | null>(null)
  const [drivers, setDrivers] = useState<DriverResponse[] | null>(null)
  const [fleetError, setFleetError] = useState(false)
  const [fleetReload, setFleetReload] = useState(0)
  const [selectedRouteId, setSelectedRouteId] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [planning, setPlanning] = useState(false)
  const [creatingOrder, setCreatingOrder] = useState(false)
  const [confirmingOrderId, setConfirmingOrderId] = useState<string | null>(null)
  const [updatingVolumeId, setUpdatingVolumeId] = useState<string | null>(null)
  const [volumeDrafts, setVolumeDrafts] = useState<Record<string, string>>({})
  const [copyingYesterday, setCopyingYesterday] = useState(false)
  const [addingOrderId, setAddingOrderId] = useState<string | null>(null)
  const [routeForOrder, setRouteForOrder] = useState<Record<string, string>>({})
  const [dragOrderId, setDragOrderId] = useState<string | null>(null)
  const [hoveredRouteId, setHoveredRouteId] = useState<string | null>(null)
  const [updatingStop, setUpdatingStop] = useState<{ stopId: string; status: DeliveryStatus } | null>(null)
  const mutationInFlight = useRef(false)
  const [reload, setReload] = useState(0)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [refreshError, setRefreshError] = useState(false)
  const [lastUpdatedAt, setLastUpdatedAt] = useState<Date | null>(null)
  const lastSuccessfulDay = useRef<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [statusError, setStatusError] = useState<string | null>(null)
  const [confirmError, setConfirmError] = useState<string | null>(null)
  const [confirmedVolumeError, setConfirmedVolumeError] = useState<string | null>(null)
  const [routeOrderError, setRouteOrderError] = useState<string | null>(null)
  const [stopOrderError, setStopOrderError] = useState<string | null>(null)
  const [draftOrder, setDraftOrder] = useState<{ routeId: string; stopIds: string[] } | null>(null)
  const [savingStopOrder, setSavingStopOrder] = useState(false)
  const [moveDraft, setMoveDraft] = useState<{
    sourceRouteId: string; stopId: string; destinationRouteId: string
  } | null>(null)
  const [movingStop, setMovingStop] = useState(false)
  const [moveError, setMoveError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  useEffect(() => {
    const hasCurrentData = lastSuccessfulDay.current === day
    setLoading(!hasCurrentData)
    if (!hasCurrentData) setLoadError(null)
    const refresh = startDayRefresh({
      day,
      getOrders,
      getRoutes,
      onSuccess: (nextOrders, nextRoutes, updatedAt) => {
        lastSuccessfulDay.current = day
        setOrders(nextOrders)
        setRoutes(nextRoutes)
        setDragOrderId(current => nextOrders.some(order => order.id === current && order.status === 'Confirmed') ? current : null)
        setLastUpdatedAt(updatedAt)
        setLoadError(null)
        setRefreshError(false)
        setLoading(false)
      },
      onFailure: error => {
        if (lastSuccessfulDay.current === day) {
          setRefreshError(true)
        } else {
          setLoadError(errorMessage(error))
        }
        setLoading(false)
      },
    })
    return refresh.stop
  }, [day, reload])

  useEffect(() => {
    const controller = new AbortController()
    let inFlight = false
    async function refreshFleet() {
      if (inFlight) return
      inFlight = true
      try {
        const [nextVehicles, nextDrivers] = await Promise.all([
          getVehicles(controller.signal), getDrivers(controller.signal),
        ])
        if (!controller.signal.aborted) {
          setVehicles(nextVehicles)
          setDrivers(nextDrivers)
          setFleetError(false)
        }
      } catch {
        if (!controller.signal.aborted) setFleetError(true)
      } finally {
        inFlight = false
      }
    }
    void refreshFleet()
    const timer = window.setInterval(() => { void refreshFleet() }, 30_000)
    return () => { controller.abort(); window.clearInterval(timer) }
  }, [fleetReload])

  async function handlePlan(orderIds?: string[]) {
    if (mutationInFlight.current) return
    mutationInFlight.current = true
    setPlanning(true)
    setActionError(null)
    setNotice(null)
    try {
      const selectedIds = orderIds ?? planableOrders.map(order => order.id)
      const result = await planRemaining(day, selectedIds)
      const addedCount = result.extendedRoutes.reduce((sum, route) => sum + route.addedStops.length, 0)
      setNotice(`${result.createdRoutes.length} ${result.createdRoutes.length === 1 ? 'rută nouă creată' : 'rute noi create'}; ${addedCount} ${addedCount === 1 ? 'comandă adăugată' : 'comenzi adăugate'} la rutele existente.`)
      setReload(value => value + 1)
    } catch (error) {
      if (error instanceof ApiError && error.status === 409) {
        setActionError(`Planificarea a fost respinsă (409): ${error.message} Verifică rutele și comenzile actualizate mai jos.`)
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
        lastSuccessfulDay.current = null
        setLastUpdatedAt(null)
        setRefreshError(false)
        setConfirmedVolumeError(null)
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
    setConfirmedVolumeError(null)
    setNotice(null)
    try {
      await confirmOrder(id)
      setNotice('Comanda a fost confirmată și este gata de planificare.')
      setLoading(true)
      setReload(value => value + 1)
    } catch (error) {
      setConfirmError(confirmErrorMessage(error))
      if (error instanceof ApiError && (error.status === 400 || error.status === 404 || error.status === 409)) {
        setLoading(true)
        setReload(value => value + 1)
        setFleetReload(value => value + 1)
      }
    } finally {
      setConfirmingOrderId(null)
      mutationInFlight.current = false
    }
  }

  async function handleUpdateVolume(id: string, volume: number) {
    if (mutationInFlight.current) throw new Error('O altă operație este în curs.')
    mutationInFlight.current = true
    setUpdatingVolumeId(id)
    try {
      const updated = await updateOrderVolume(id, volume)
      setOrders(current => current.map(order => order.id === id ? updated : order))
      setVolumeDrafts(current => { const next = { ...current }; delete next[id]; return next })
      setNotice('Volumul a fost salvat. Poți confirma comanda.')
      setReload(value => value + 1)
    } catch (error) {
      if (error instanceof ApiError && (error.status === 404 || error.status === 409))
        setReload(value => value + 1)
      throw new Error(errorMessage(error))
    } finally {
      setUpdatingVolumeId(null)
      mutationInFlight.current = false
    }
  }

  async function handleCorrectConfirmedVolume(id: string, volume: number, expectedVolume: number) {
    if (mutationInFlight.current) throw new Error('O altă operație este în curs.')
    mutationInFlight.current = true
    setUpdatingVolumeId(id)
    setNotice(null)
    setConfirmedVolumeError(null)
    try {
      const updated = await correctConfirmedVolume(id, volume, expectedVolume)
      setOrders(current => current.map(order => order.id === id ? updated : order))
      setNotice('Volumul comenzii confirmate a fost corectat. Acum o poți planifica.')
      setReload(value => value + 1)
    } catch (error) {
      setConfirmedVolumeError(errorMessage(error))
      if (error instanceof ApiError && (error.status === 404 || error.status === 409))
        setReload(value => value + 1)
      throw new Error(errorMessage(error))
    } finally {
      setUpdatingVolumeId(null)
      mutationInFlight.current = false
    }
  }

  async function handleAddToRoute(orderId: string, routeId: string) {
    if (mutationInFlight.current || !routeId) return
    const order = orders.find(item => item.id === orderId)
    const route = routes.find(item => item.id === routeId)
    if (!order || !route || !routeAdditionCheck(order, route).accepted) return
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

  function handleDropOrder(event: DragEvent<HTMLElement>, route: RouteResponse) {
    if (!dragOrderId) return
    event.preventDefault()
    const orderId = event.dataTransfer.getData('application/x-logistics-order-id')
    const order = orders.find(item => item.id === orderId)
    setDragOrderId(null)
    setHoveredRouteId(null)
    if (!order || order.id !== dragOrderId) return
    const check = routeAdditionCheck(order, route)
    if (!check.accepted) {
      setRouteOrderError(`Comanda nu poate fi adăugată la ruta aleasă: ${check.reason}`)
      return
    }
    void handleAddToRoute(order.id, route.id)
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

  async function handleMoveStop() {
    if (!moveDraft?.destinationRouteId || mutationInFlight.current) return
    const { sourceRouteId, stopId, destinationRouteId } = moveDraft
    mutationInFlight.current = true
    setMovingStop(true)
    setMoveError(null)
    setNotice(null)
    let moved = false
    try {
      await moveRouteStop(sourceRouteId, stopId, destinationRouteId)
      moved = true
      const [source, destination] = await Promise.all([
        getRoute(sourceRouteId), getRoute(destinationRouteId),
      ])
      setRoutes(current => current.map(route =>
        route.id === source.id ? source : route.id === destination.id ? destination : route))
      setMoveDraft(null)
      setNotice('Oprirea a fost mutată în ruta destinație.')
    } catch (error) {
      if (moved) {
        setMoveDraft(null)
        setMoveError('Mutarea a fost salvată, dar rutele nu au putut fi reîncărcate. Reîncarcă datele înainte de o altă mutare.')
      } else {
        setMoveError(moveStopErrorMessage(error))
      }
    } finally {
      setMovingStop(false)
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
    lastSuccessfulDay.current = null
    setLastUpdatedAt(null)
    setRefreshError(false)
    setActionError(null)
    setStatusError(null)
    setConfirmError(null)
    setConfirmedVolumeError(null)
    setRouteOrderError(null)
    setStopOrderError(null)
    setDraftOrder(null)
    setMoveError(null)
    setMoveDraft(null)
    setRouteForOrder({})
    setVolumeDrafts({})
    setDragOrderId(null)
    setHoveredRouteId(null)
    setNotice(null)
  }

  const confirmedOrders = orders.filter(order => order.status === 'Confirmed')
  const planabilityByOrder = new Map(confirmedOrders.map(order =>
    [order.id, orderPlanability(order, routes, vehicles, drivers)]))
  const planableOrders = confirmedOrders.filter(order => planabilityByOrder.get(order.id)?.actionable)
  const blockedOrders = confirmedOrders.filter(order => !planabilityByOrder.get(order.id)?.actionable)
  const maxVehicleCapacity = vehicles === null ? null : Math.max(0, ...vehicles.map(vehicle => vehicle.capacity))
  const stopCount = routes.reduce((sum, route) => sum + route.stops.length, 0)
  const selectedRoute = routes.find(route => route.id === selectedRouteId) ?? routes[0]
  const draggedOrder = orders.find(order => order.id === dragOrderId && order.status === 'Confirmed')
  const selectedProgress = selectedRoute ? routeProgress(selectedRoute.stops) : null
  const selectedCapacity = selectedRoute ? capacityUsage(selectedRoute) : null
  const busy = planning || creatingOrder || confirmingOrderId !== null || updatingVolumeId !== null || copyingYesterday ||
    updatingStop !== null || addingOrderId !== null || savingStopOrder || draftOrder !== null ||
    movingStop || moveDraft !== null

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
          <button className="primary-button" type="button" onClick={() => { void handlePlan() }}
            disabled={loading || busy || !!loadError || planableOrders.length === 0}>
            <span aria-hidden="true">✦</span> {planning ? 'Se planifică…' : routes.length === 0
              ? 'Planifică comenzile eligibile' : 'Planifică comenzile rămase'}
          </button>
        </section>

        <div className="day-refresh-status" role="status" aria-live="polite">
          <span>{lastUpdatedAt
            ? `Comenzi și rute actualizate la ${lastUpdatedAt.toLocaleTimeString('ro-MD', { hour: '2-digit', minute: '2-digit', second: '2-digit' })}`
            : 'Se așteaptă prima actualizare a comenzilor și rutelor.'}</span>
          {refreshError && <span className="day-refresh-warning">Actualizarea a eșuat; se afișează ultimele date primite.</span>}
        </div>

        {loadError && <div className="alert alert-error" role="alert">
          <strong>Nu am putut încărca datele.</strong> {loadError}
          <button type="button" onClick={() => setReload(value => value + 1)}>Reîncearcă</button>
        </div>}
        {actionError && <div className="alert alert-error" role="alert"><strong>Planificarea a eșuat.</strong> {actionError}</div>}
        {notice && <div className="alert alert-success" role="status">{notice}</div>}

        <div className="summary-grid" aria-label="Rezumatul zilei">
          <div className="summary-card"><span>COMENZI</span><strong>{loading ? '—' : orders.length}</strong><small>în ziua selectată</small></div>
          <div className="summary-card"><span>DE PLANIFICAT</span><strong>{loading ? '—' : planableOrders.length}</strong><small>{blockedOrders.length > 0 ? `${blockedOrders.length} confirmate blocate · vezi motivele` : 'comenzi confirmate cu acțiune disponibilă'}</small></div>
          <div className="summary-card"><span>RUTE</span><strong>{loading ? '—' : routes.length}</strong><small>{loading ? 'se încarcă' : `${stopCount} opriri în total`}</small></div>
        </div>

        <ResourceManagement onResourceSaved={() => setFleetReload(value => value + 1)} />
        {fleetError && <p className="day-refresh-warning" role="status">Flota nu a putut fi actualizată; confirmarea și planificarea vor fi verificate de API. <button type="button" onClick={() => setFleetReload(value => value + 1)}>Reîncearcă</button></p>}

        <CopyYesterdayPanel key={day} day={day} busy={busy} onPendingChange={setCopyingYesterday} onCopied={result => {
          setNotice(`${result.created.length} comenzi New create din ziua precedentă.`)
          setReload(value => value + 1)
        }} />

        <section className="panel new-order-panel" aria-labelledby="new-order-title">
          <div className="panel-heading"><div><p className="section-kicker">ADĂUGARE COMANDĂ</p><h2 id="new-order-title">Comandă nouă</h2></div></div>
          <NewOrderForm selectedDay={day} busy={busy} onSave={handleCreateOrder} />
        </section>

        <div className="content-grid">
          <section className="panel orders-panel" aria-labelledby="orders-title">
            <div className="panel-heading"><div><p className="section-kicker">01 / COMENZI</p><h2 id="orders-title">Comenzile zilei</h2></div><span className="count-pill">{loading ? '…' : orders.length}</span></div>
            {confirmError && <div className="status-error" role="alert">{confirmError}</div>}
            {confirmedVolumeError && <div className="status-error" role="alert">{confirmedVolumeError}</div>}
            {routeOrderError && <div className="status-error" role="alert">{routeOrderError}</div>}
            {loading ? <p className="state-message" role="status">Se încarcă comenzile…</p>
              : loadError ? <p className="state-message">Comenzile nu sunt disponibile.</p>
              : orders.length === 0 ? <p className="state-message">Nu există comenzi pentru ziua selectată.</p>
              : <div className="table-scroll"><table>
                <thead><tr><th>Adresă / zonă</th><th>Volum</th><th>Status</th></tr></thead>
                <tbody>{orders.map(order => {
                  const availableRoutes = order.status === 'Confirmed' ? compatibleRoutes(order, routes) : []
                  const chosenRouteId = availableRoutes.some(route => route.id === routeForOrder[order.id])
                    ? routeForOrder[order.id]
                    : availableRoutes.find(route => routeAdditionCheck(order, route).accepted)?.id ?? availableRoutes[0]?.id ?? ''
                  const chosenRoute = availableRoutes.find(route => route.id === chosenRouteId)
                  const preview = chosenRoute ? additionPreview(chosenRoute, order) : null
                  const planability = planabilityByOrder.get(order.id)
                  return <tr key={order.id} className={dragOrderId === order.id ? 'is-being-dragged' : undefined}>
                  <td><strong>{order.address}</strong><span className="secondary-line">{order.zone}</span>
                    {order.sourceOrderId && <span className="secondary-line">Copiată din comanda de ieri</span>}
                    {order.status === 'Confirmed' && <span className="order-drag-handle"
                      draggable={!busy && !loading}
                      onDragStart={event => {
                        if (busy || loading) { event.preventDefault(); return }
                        event.dataTransfer.setData('application/x-logistics-order-id', order.id)
                        event.dataTransfer.effectAllowed = 'copy'
                        setDragOrderId(order.id)
                        setHoveredRouteId(null)
                        setRouteOrderError(null)
                      }}
                      onDragEnd={() => { setDragOrderId(null); setHoveredRouteId(null) }}
                      title={`Trage comanda ${order.address} pe o rută`}>
                      Trage pe o rută
                    </span>}
                  </td>
                  <td className="number-cell">{order.status === 'New'
                    ? <NewOrderVolume order={order} busy={busy || loading} onSave={handleUpdateVolume}
                      value={volumeDrafts[order.id] ?? String(order.volume)}
                      onValueChange={value => setVolumeDrafts(current => ({ ...current, [order.id]: value }))}
                      maxCapacity={maxVehicleCapacity} />
                    : volumeFormat.format(order.volume)}</td>
                  <td className="order-status-cell"><span className={`status status-${order.status.toLowerCase()}`}>{orderLabels[order.status] ?? order.status}</span>
                    {order.status === 'New' && <button type="button" className="confirm-button"
                      onClick={() => handleConfirmOrder(order.id)} disabled={busy || loading ||
                        (maxVehicleCapacity !== null && order.volume > maxVehicleCapacity) ||
                        (volumeDrafts[order.id] !== undefined &&
                          Number(volumeDrafts[order.id].trim().replace(',', '.')) !== order.volume)}
                      aria-label={`Confirmă comanda ${order.address}`}>
                      {confirmingOrderId === order.id ? 'Se confirmă…' : 'Confirmă'}
                    </button>}
                    {order.status === 'New' && volumeDrafts[order.id] !== undefined &&
                      Number(volumeDrafts[order.id].trim().replace(',', '.')) !== order.volume &&
                      <small className="route-unavailable">Salvează volumul înainte de confirmare.</small>}
                    {order.status === 'Confirmed' && (availableRoutes.length > 0
                      ? <div className="add-to-route-controls">
                        <select aria-label={`Rută pentru comanda ${order.address}`} value={chosenRouteId}
                          onChange={event => { setRouteForOrder(current => ({ ...current, [order.id]: event.target.value })); setRouteOrderError(null) }}
                          disabled={busy || loading}>
                          {availableRoutes.map(route => <option value={route.id} key={route.id}>
                            Ruta {routes.indexOf(route) + 1} · {route.vehicle.registrationNumber} · liber {volumeFormat.format(Math.max(0, route.vehicle.capacity - route.totalVolume))}
                          </option>)}
                        </select>
                        <span className={`capacity-preview${preview?.exceeds ? ' is-over' : ''}`} role="status">
                          {preview && chosenRoute
                            ? `După adăugare: ${volumeFormat.format(preview.projected)} / ${volumeFormat.format(chosenRoute.vehicle.capacity)} · ${preview.exceeds
                              ? `depășire cu ${volumeFormat.format(-preview.remaining)}`
                              : `rămân ${volumeFormat.format(preview.remaining)}`}`
                            : 'Capacitatea nu poate fi calculată din datele primite.'}
                        </span>
                        <button type="button" className="confirm-button" onClick={() => handleAddToRoute(order.id, chosenRouteId)}
                          disabled={busy || loading || !chosenRoute || !routeAdditionCheck(order, chosenRoute).accepted}>
                          {addingOrderId === order.id ? 'Se adaugă…' : 'Adaugă la rută'}
                        </button>
                      </div>
                      : <span className="route-unavailable">{unavailableRouteReason(order, routes)}</span>)}
                    {order.status === 'Confirmed' && planability?.newRoutePossible && planability.addableRouteIds.length === 0 &&
                      <button type="button" className="confirm-button" disabled={busy || loading}
                        onClick={() => { void handlePlan([order.id]) }}>
                        Creează rută nouă pentru această comandă
                      </button>}
                    {order.status === 'Confirmed' && !planability?.actionable &&
                      <span className="route-unavailable" role="status">{planability?.reason}</span>}
                    {order.status === 'Confirmed' && maxVehicleCapacity !== null && maxVehicleCapacity > 0 &&
                      order.volume > maxVehicleCapacity &&
                      !routes.some(route => route.stops.some(stop => stop.orderId === order.id)) &&
                      <ConfirmedVolumeCorrection order={order} maxCapacity={maxVehicleCapacity}
                        busy={busy || loading} onSave={handleCorrectConfirmedVolume} />}
                  </td>
                </tr>})}</tbody>
              </table></div>}
          </section>

          <section className="panel routes-panel" aria-labelledby="routes-title">
            <div className="panel-heading"><div><p className="section-kicker">02 / RUTE</p><h2 id="routes-title">Rutele zilei</h2></div><span className="count-pill">{loading ? '…' : routes.length}</span></div>
            {draggedOrder && <p className="drag-instruction">Trage „{draggedOrder.address}” pe o rută verde. Rutele care nu acceptă comanda arată motivul.</p>}
            {statusError && <div className="status-error" role="alert">{statusError}</div>}
            {stopOrderError && <div className="status-error" role="alert">{stopOrderError}</div>}
            {moveError && <div className="status-error" role="alert">{moveError}</div>}
            {loading ? <p className="state-message" role="status">Se încarcă rutele…</p>
              : loadError ? <p className="state-message">Rutele nu sunt disponibile.</p>
              : routes.length === 0 ? <div className="empty-routes"><div className="empty-icon" aria-hidden="true">⌁</div><strong>Nu există rute planificate</strong><p>Rutele pentru această zi vor apărea aici după planificare.</p></div>
              : <div className="route-list">{routes.map((route, index) => {
                const dropCheck = draggedOrder ? routeAdditionCheck(draggedOrder, route) : null
                const editing = draftOrder?.routeId === route.id
                const canReorder = route.stops.length > 1 && route.stops.every(stop => stop.deliveryStatus === 'Pending')
                const savedStops = [...route.stops].sort((a, b) => a.sequence - b.sequence)
                const orderedStops = editing
                  ? draftOrder.stopIds.map(id => route.stops.find(stop => stop.id === id)!).filter(Boolean)
                  : savedStops
                const changed = editing && orderedStops.some((stop, position) => stop.id !== savedStops[position]?.id)
                const destinationRoutes = routes.filter(candidate => candidate.id !== route.id && candidate.date === day)
                return <article className={`route-card${selectedRoute?.id === route.id ? ' is-selected' : ''}${dropCheck ? dropCheck.accepted ? ' is-drop-compatible' : ' is-drop-incompatible' : ''}${dropCheck && hoveredRouteId === route.id ? ' is-drop-hovered' : ''}`}
                  key={route.id}
                  onDragOver={event => {
                    if (!draggedOrder || busy || loading) return
                    event.preventDefault()
                    event.dataTransfer.dropEffect = dropCheck?.accepted ? 'copy' : 'none'
                    if (hoveredRouteId !== route.id) setHoveredRouteId(route.id)
                  }}
                  onDragLeave={event => {
                    if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setHoveredRouteId(null)
                  }}
                  onDrop={event => handleDropOrder(event, route)}>
                <div className="route-header"><div><span className="route-index">RUTA {String(index + 1).padStart(2, '0')}</span><h3>{route.stops[0]?.zone || 'Rută'}</h3></div><div className="route-header-actions"><span className="stop-count">{route.stops.length} {route.stops.length === 1 ? 'oprire' : 'opriri'}</span><button type="button" className="route-select-button" aria-pressed={selectedRoute?.id === route.id} onClick={() => { setSelectedRouteId(route.id); setStatusError(null) }} disabled={busy}>{selectedRoute?.id === route.id ? 'Pe hartă' : 'Vezi pe hartă'}</button></div></div>
                {dropCheck && <p className={`route-drop-message${dropCheck.accepted ? ' accepts' : ' rejects'}`}>
                  {dropCheck.accepted ? 'Poți lăsa comanda aici.' : `Nu acceptă comanda: ${dropCheck.reason}`}
                </p>}
                <div className="route-facts"><div><span>VEHICUL</span><strong>{route.vehicle.registrationNumber}</strong></div><div><span>ȘOFER</span><strong>{route.driver.fullName}</strong></div><div><span>VOLUM TOTAL</span><strong>{volumeFormat.format(route.totalVolume)}</strong></div></div>
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
                  <span className="stop-detail"><strong>{stop.address}</strong><small>{stop.zone} · {volumeFormat.format(stop.volume)} · {deliveryLabels[stop.deliveryStatus] ?? stop.deliveryStatus}</small>
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
                      {stop.deliveryStatus === 'Pending' && <button type="button"
                        disabled={busy || loading}
                        onClick={() => {
                          setMoveDraft({ sourceRouteId: route.id, stopId: stop.id,
                            destinationRouteId: destinationRoutes[0]?.id ?? '' })
                          setMoveError(null)
                        }}>
                        Mută în altă rută
                      </button>}
                    </span>}
                    {moveDraft?.sourceRouteId === route.id && moveDraft.stopId === stop.id &&
                      <span className="move-controls">
                        {destinationRoutes.length === 0
                          ? <span>Nu există altă rută în ziua selectată.</span>
                          : <label>Rută destinație
                            <select value={moveDraft.destinationRouteId} disabled={movingStop}
                              onChange={event => {
                                setMoveDraft(current => current && { ...current, destinationRouteId: event.target.value })
                                setMoveError(null)
                              }}>
                              {destinationRoutes.map(candidate => <option key={candidate.id} value={candidate.id}>
                                Ruta {routes.indexOf(candidate) + 1} · {candidate.vehicle.registrationNumber} · {candidate.stops[0]?.zone ?? 'fără zonă'}
                              </option>)}
                            </select>
                          </label>}
                        <span className="move-buttons">
                          <button type="button" className="route-select-button" disabled={movingStop}
                            onClick={() => { setMoveDraft(null); setMoveError(null) }}>Anulează</button>
                          <button type="button" className="confirm-button"
                            disabled={!moveDraft.destinationRouteId || movingStop}
                            onClick={handleMoveStop}>{movingStop ? 'Se mută…' : 'Confirmă mutarea'}</button>
                        </span>
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
          {selectedProgress && !loading && !loadError && <div className="route-progress" aria-label="Progresul rutei selectate">
            <div className="route-progress-total"><strong>{selectedProgress.finished} / {selectedProgress.total}</strong><span>opriri încheiate</span></div>
            <div className="route-progress-breakdown">
              <span className="progress-delivered">Livrate <strong>{selectedProgress.delivered}</strong></span>
              <span className="progress-refused">Refuzate <strong>{selectedProgress.refused}</strong></span>
              <span className="progress-partialreturn">Retur parțial <strong>{selectedProgress.partialReturn}</strong></span>
            </div>
          </div>}
          {selectedRoute && !loading && !loadError && <div className="route-capacity" aria-label="Capacitatea rutei selectate">
              <div className="route-capacity-heading"><strong>Capacitate vehicul</strong>
                <span>{selectedCapacity ? `${volumeFormat.format(selectedCapacity.total)} / ${volumeFormat.format(selectedCapacity.capacity)} · ${volumeFormat.format(selectedCapacity.percent)}%` : 'Date indisponibile'}</span>
              </div>
              {selectedCapacity && <><div className="capacity-track" role="progressbar" aria-label="Capacitate utilizată"
                aria-valuemin={0} aria-valuemax={selectedCapacity.capacity} aria-valuenow={Math.min(selectedCapacity.total, selectedCapacity.capacity)}
                aria-valuetext={`${volumeFormat.format(selectedCapacity.total)} din ${volumeFormat.format(selectedCapacity.capacity)}`}>
                <span className={selectedCapacity.remaining < 0 ? 'is-over' : ''} style={{ width: `${Math.min(100, selectedCapacity.percent)}%` }} />
              </div><span className={`capacity-remaining${selectedCapacity.remaining < 0 ? ' is-over' : ''}`}>
                {selectedCapacity.remaining < 0 ? `Depășire: ${volumeFormat.format(-selectedCapacity.remaining)}` : `Spațiu rămas: ${volumeFormat.format(selectedCapacity.remaining)}`}
              </span></>}
            </div>
          }
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

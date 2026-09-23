import { useEffect, useRef, useState, type FormEvent } from 'react'
import {
  ApiError,
  createDriver,
  createVehicle,
  getDrivers,
  getVehicles,
  type DriverResponse,
  type VehicleResponse,
} from './api'
import { numberFormat } from './format'

function useResourceList<T>(getItems: (signal?: AbortSignal) => Promise<T[]>) {
  const [items, setItems] = useState<T[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [reload, setReload] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    setLoading(true)
    setError(null)
    getItems(controller.signal)
      .then(result => {
        if (!controller.signal.aborted) setItems(result)
      })
      .catch(() => {
        if (!controller.signal.aborted) setError('Lista nu a putut fi încărcată. Verifică conexiunea și încearcă din nou.')
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })
    return () => controller.abort()
  }, [getItems, reload])

  function refresh() {
    setLoading(true)
    setReload(value => value + 1)
  }

  return { items, loading, error, refresh }
}

function saveErrorMessage(error: unknown, resource: string): string {
  if (error instanceof ApiError) {
    if (error.status === 400) return `${resource} a fost respins. Verifică datele introduse.`
    return `Nu am putut salva ${resource.toLowerCase()} (eroarea ${error.status}). Încearcă din nou.`
  }
  return 'Conexiunea cu API-ul a eșuat. Verifică dacă API-ul rulează și încearcă din nou.'
}

function VehicleManagement() {
  const { items, loading, error, refresh } = useResourceList(getVehicles)
  const [registrationNumber, setRegistrationNumber] = useState('')
  const [capacity, setCapacity] = useState('')
  const [fieldErrors, setFieldErrors] = useState<{ registrationNumber?: string; capacity?: string }>({})
  const [saveError, setSaveError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const submittingRef = useRef(false)

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (submittingRef.current || loading) return

    const errors: typeof fieldErrors = {}
    const normalizedCapacity = capacity.trim().replace(',', '.')
    const parsedCapacity = Number(normalizedCapacity)
    if (!registrationNumber.trim()) errors.registrationNumber = 'Introdu numărul de înmatriculare.'
    if (!/^\d{1,15}(?:\.\d{1,3})?$/.test(normalizedCapacity) || !Number.isFinite(parsedCapacity) || parsedCapacity <= 0)
      errors.capacity = 'Capacitatea trebuie să fie mai mare decât 0, cu cel mult 3 zecimale.'
    setFieldErrors(errors)
    setSaveError(null)
    setNotice(null)
    if (Object.keys(errors).length > 0) return

    submittingRef.current = true
    setSubmitting(true)
    try {
      await createVehicle({ registrationNumber: registrationNumber.trim(), capacity: parsedCapacity })
      setRegistrationNumber('')
      setCapacity('')
      setNotice('Vehiculul a fost adăugat.')
      refresh()
    } catch (saveFailure) {
      setSaveError(saveErrorMessage(saveFailure, 'Vehiculul'))
    } finally {
      submittingRef.current = false
      setSubmitting(false)
    }
  }

  return <section className="panel resource-panel" aria-labelledby="vehicles-title">
    <div className="panel-heading"><div><p className="section-kicker">VEHICULE</p><h2 id="vehicles-title">Vehicule disponibile</h2></div><span className="count-pill">{loading ? '…' : items.length}</span></div>
    <form className="resource-form" onSubmit={submit} noValidate>
      <fieldset disabled={submitting || loading}>
        <div className="resource-fields">
          <label className="order-field"><span>Număr de înmatriculare</span>
            <input name="registrationNumber" autoComplete="off" value={registrationNumber}
              onChange={event => { setRegistrationNumber(event.target.value); setFieldErrors(current => ({ ...current, registrationNumber: undefined })); setSaveError(null) }}
              aria-invalid={!!fieldErrors.registrationNumber} aria-describedby={fieldErrors.registrationNumber ? 'registration-error' : undefined} />
            {fieldErrors.registrationNumber && <small id="registration-error" role="alert">{fieldErrors.registrationNumber}</small>}
          </label>
          <label className="order-field"><span>Capacitate</span>
            <input name="capacity" inputMode="decimal" value={capacity}
              onChange={event => { setCapacity(event.target.value); setFieldErrors(current => ({ ...current, capacity: undefined })); setSaveError(null) }}
              aria-invalid={!!fieldErrors.capacity} aria-describedby={fieldErrors.capacity ? 'capacity-error' : undefined} />
            {fieldErrors.capacity && <small id="capacity-error" role="alert">{fieldErrors.capacity}</small>}
          </label>
        </div>
        {saveError && <p className="order-form-error" role="alert">{saveError}</p>}
        {notice && <p className="resource-notice" role="status">{notice}</p>}
        <button type="submit" className="primary-button">{submitting ? 'Se salvează…' : 'Adaugă vehicul'}</button>
      </fieldset>
    </form>
    {error && <div className="resource-load-error" role="alert">{error} <button type="button" onClick={refresh}>Reîncearcă</button></div>}
    {loading ? <p className="state-message" role="status">Se încarcă vehiculele…</p>
      : error ? null
      : items.length === 0 ? <p className="state-message">Nu există vehicule înregistrate.</p>
      : <div className="table-scroll"><table><thead><tr><th>Număr de înmatriculare</th><th>Capacitate</th></tr></thead>
        <tbody>{items.map((vehicle: VehicleResponse) => <tr key={vehicle.id}><td><strong>{vehicle.registrationNumber}</strong></td><td className="number-cell">{numberFormat.format(vehicle.capacity)}</td></tr>)}</tbody>
      </table></div>}
  </section>
}

function DriverManagement() {
  const { items, loading, error, refresh } = useResourceList(getDrivers)
  const [fullName, setFullName] = useState('')
  const [fieldError, setFieldError] = useState<string | null>(null)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const submittingRef = useRef(false)

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (submittingRef.current || loading) return
    if (!fullName.trim()) {
      setFieldError('Introdu numele complet al șoferului.')
      setSaveError(null)
      setNotice(null)
      return
    }

    submittingRef.current = true
    setSubmitting(true)
    setFieldError(null)
    setSaveError(null)
    setNotice(null)
    try {
      await createDriver({ fullName: fullName.trim() })
      setFullName('')
      setNotice('Șoferul a fost adăugat.')
      refresh()
    } catch (saveFailure) {
      setSaveError(saveErrorMessage(saveFailure, 'Șoferul'))
    } finally {
      submittingRef.current = false
      setSubmitting(false)
    }
  }

  return <section className="panel resource-panel" aria-labelledby="drivers-title">
    <div className="panel-heading"><div><p className="section-kicker">ȘOFERI</p><h2 id="drivers-title">Șoferi disponibili</h2></div><span className="count-pill">{loading ? '…' : items.length}</span></div>
    <form className="resource-form" onSubmit={submit} noValidate>
      <fieldset disabled={submitting || loading}>
        <div className="resource-fields">
          <label className="order-field"><span>Nume complet</span>
            <input name="fullName" autoComplete="name" value={fullName}
              onChange={event => { setFullName(event.target.value); setFieldError(null); setSaveError(null) }}
              aria-invalid={!!fieldError} aria-describedby={fieldError ? 'driver-name-error' : undefined} />
            {fieldError && <small id="driver-name-error" role="alert">{fieldError}</small>}
          </label>
        </div>
        {saveError && <p className="order-form-error" role="alert">{saveError}</p>}
        {notice && <p className="resource-notice" role="status">{notice}</p>}
        <button type="submit" className="primary-button">{submitting ? 'Se salvează…' : 'Adaugă șofer'}</button>
      </fieldset>
    </form>
    {error && <div className="resource-load-error" role="alert">{error} <button type="button" onClick={refresh}>Reîncearcă</button></div>}
    {loading ? <p className="state-message" role="status">Se încarcă șoferii…</p>
      : error ? null
      : items.length === 0 ? <p className="state-message">Nu există șoferi înregistrați.</p>
      : <div className="table-scroll"><table><thead><tr><th>Nume complet</th></tr></thead>
        <tbody>{items.map((driver: DriverResponse) => <tr key={driver.id}><td><strong>{driver.fullName}</strong></td></tr>)}</tbody>
      </table></div>}
  </section>
}

export default function ResourceManagement() {
  return <div className="resource-grid" aria-label="Administrarea vehiculelor și șoferilor">
    <VehicleManagement />
    <DriverManagement />
  </div>
}

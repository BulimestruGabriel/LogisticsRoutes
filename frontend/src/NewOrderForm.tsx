import { useEffect, useRef, useState, type FormEvent } from 'react'
import { ApiError, type CreateOrderRequest, type OrderResponse } from './api'

interface FormValues {
  zone: string
  address: string
  latitude: string
  longitude: string
  volume: string
  deliveryDate: string
}

type FormErrors = Partial<Record<keyof FormValues, string>>

function emptyForm(day: string): FormValues {
  return { zone: '', address: '', latitude: '', longitude: '', volume: '', deliveryDate: day }
}

function parseDecimal(value: string): number | null {
  const normalized = value.trim().replace(',', '.')
  if (!/^[+-]?(?:\d+\.?\d*|\.\d+)$/.test(normalized)) return null
  const number = Number(normalized)
  return Number.isFinite(number) ? number : null
}

function validDay(value: string): boolean {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value) || value === '0001-01-01') return false
  const parsed = new Date(`${value}T00:00:00Z`)
  return !Number.isNaN(parsed.getTime()) && parsed.toISOString().slice(0, 10) === value
}

function validate(values: FormValues): { errors: FormErrors; request: CreateOrderRequest | null } {
  const errors: FormErrors = {}
  const latitude = parseDecimal(values.latitude)
  const longitude = parseDecimal(values.longitude)
  const volume = parseDecimal(values.volume)

  if (!values.zone.trim()) errors.zone = 'Introdu o zonă.'
  if (!values.address.trim()) errors.address = 'Introdu o adresă.'
  if (latitude === null || latitude < -90 || latitude > 90)
    errors.latitude = 'Latitudinea trebuie să fie un număr între −90 și 90.'
  if (longitude === null || longitude < -180 || longitude > 180)
    errors.longitude = 'Longitudinea trebuie să fie un număr între −180 și 180.'
  if (volume === null || volume <= 0)
    errors.volume = 'Volumul trebuie să fie un număr mai mare decât 0.'
  if (!validDay(values.deliveryDate)) errors.deliveryDate = 'Alege o zi de livrare validă.'

  return {
    errors,
    request: Object.keys(errors).length === 0 ? {
      zone: values.zone.trim(),
      address: values.address.trim(),
      latitude: latitude!,
      longitude: longitude!,
      volume: volume!,
      deliveryDate: values.deliveryDate,
    } : null,
  }
}

function saveErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 400)
      return 'Comanda a fost respinsă. Verifică zona, adresa, coordonatele, volumul și ziua livrării.'
    return `API-ul nu a putut salva comanda (eroarea ${error.status}). Încearcă din nou.`
  }
  return 'Conexiunea cu API-ul a eșuat. Verifică dacă API-ul rulează și încearcă din nou.'
}

export default function NewOrderForm({ selectedDay, busy, onSave }: {
  selectedDay: string
  busy: boolean
  onSave: (request: CreateOrderRequest) => Promise<OrderResponse>
}) {
  const [values, setValues] = useState<FormValues>(() => emptyForm(selectedDay))
  const [errors, setErrors] = useState<FormErrors>({})
  const [saveError, setSaveError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const submittingRef = useRef(false)

  useEffect(() => {
    setValues(current => ({ ...current, deliveryDate: selectedDay }))
    setErrors(current => ({ ...current, deliveryDate: undefined }))
  }, [selectedDay])

  function change(field: keyof FormValues, value: string) {
    setValues(current => ({ ...current, [field]: value }))
    setErrors(current => ({ ...current, [field]: undefined }))
    setSaveError(null)
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (submittingRef.current || busy) return
    const result = validate(values)
    setErrors(result.errors)
    setSaveError(null)
    if (!result.request) return

    submittingRef.current = true
    setSubmitting(true)
    try {
      const created = await onSave(result.request)
      setValues(emptyForm(created.deliveryDate))
      setErrors({})
    } catch (error) {
      setSaveError(saveErrorMessage(error))
    } finally {
      submittingRef.current = false
      setSubmitting(false)
    }
  }

  return <form className="new-order-form" onSubmit={submit} noValidate>
    <fieldset disabled={busy || submitting}>
      <div className="order-form-grid">
        <label className="order-field"><span>Zonă</span>
          <input name="zone" value={values.zone} onChange={event => change('zone', event.target.value)}
            aria-invalid={!!errors.zone} aria-describedby={errors.zone ? 'zone-error' : undefined} />
          {errors.zone && <small id="zone-error" role="alert">{errors.zone}</small>}
        </label>
        <label className="order-field order-address"><span>Adresă</span>
          <input name="address" value={values.address} onChange={event => change('address', event.target.value)}
            aria-invalid={!!errors.address} aria-describedby={errors.address ? 'address-error' : undefined} />
          {errors.address && <small id="address-error" role="alert">{errors.address}</small>}
        </label>
        <label className="order-field"><span>Latitudine</span>
          <input name="latitude" inputMode="decimal" value={values.latitude}
            onChange={event => change('latitude', event.target.value)}
            aria-invalid={!!errors.latitude} aria-describedby={errors.latitude ? 'latitude-error' : undefined} />
          {errors.latitude && <small id="latitude-error" role="alert">{errors.latitude}</small>}
        </label>
        <label className="order-field"><span>Longitudine</span>
          <input name="longitude" inputMode="decimal" value={values.longitude}
            onChange={event => change('longitude', event.target.value)}
            aria-invalid={!!errors.longitude} aria-describedby={errors.longitude ? 'longitude-error' : undefined} />
          {errors.longitude && <small id="longitude-error" role="alert">{errors.longitude}</small>}
        </label>
        <label className="order-field"><span>Volum</span>
          <input name="volume" inputMode="decimal" value={values.volume}
            onChange={event => change('volume', event.target.value)}
            aria-invalid={!!errors.volume} aria-describedby={errors.volume ? 'volume-error' : undefined} />
          {errors.volume && <small id="volume-error" role="alert">{errors.volume}</small>}
        </label>
        <label className="order-field"><span>Ziua livrării</span>
          <input name="deliveryDate" type="date" value={values.deliveryDate}
            onChange={event => change('deliveryDate', event.target.value)}
            aria-invalid={!!errors.deliveryDate}
            aria-describedby={errors.deliveryDate ? 'delivery-date-error' : undefined} />
          {errors.deliveryDate && <small id="delivery-date-error" role="alert">{errors.deliveryDate}</small>}
        </label>
      </div>
      {saveError && <p className="order-form-error" role="alert">{saveError}</p>}
      <div className="order-form-footer">
        <span>Comanda nouă poate fi confirmată după salvare.</span>
        <button type="submit" className="primary-button">{submitting ? 'Se salvează…' : 'Salvează comanda'}</button>
      </div>
    </fieldset>
  </form>
}

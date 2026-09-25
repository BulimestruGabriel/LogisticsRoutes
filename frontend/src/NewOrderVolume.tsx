import { useState } from 'react'
import type { OrderResponse } from './api'
import { volumeFormat } from './format'

export default function NewOrderVolume({ order, busy, onSave, value, onValueChange, maxCapacity }: {
  order: OrderResponse
  busy: boolean
  onSave: (id: string, volume: number) => Promise<void>
  value: string
  onValueChange: (value: string) => void
  maxCapacity: number | null
}) {
  const [error, setError] = useState<string | null>(null)
  const parsedValue = Number(value.trim().replace(',', '.'))
  const overCapacity = maxCapacity !== null && maxCapacity > 0 && Number.isFinite(parsedValue) &&
    parsedValue > maxCapacity

  async function save() {
    const normalized = value.trim().replace(',', '.')
    const parsed = Number(normalized)
    if (!/^\d+(?:\.\d{1,3})?$/.test(normalized) || !Number.isFinite(parsed) || parsed <= 0) {
      setError('Introdu un volum pozitiv, cu cel mult 3 zecimale.')
      return
    }
    setError(null)
    try {
      await onSave(order.id, parsed)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Volumul nu a putut fi salvat.')
    }
  }

  return <div className="new-volume-editor">
    <label><span>Volum New</span><input type="text" inputMode="decimal" value={value}
      onChange={event => { onValueChange(event.target.value); setError(null) }} disabled={busy}
      aria-label={`Volum pentru comanda ${order.address}`} /></label>
    <button type="button" className="confirm-button" onClick={save}
      disabled={busy || Number(value.replace(',', '.')) === order.volume}>Salvează volum</button>
    {overCapacity && <small className="volume-capacity-warning" role="status">Volumul depășește capacitatea maximă a unui vehicul ({volumeFormat.format(maxCapacity ?? 0)}). Corectează volumul sau adaugă un vehicul potrivit înainte de confirmare.</small>}
    {maxCapacity === 0 && <small className="volume-capacity-warning" role="status">Nu există vehicule în flotă; comanda nu poate fi confirmată încă.</small>}
    {error && <small role="alert">{error}</small>}
  </div>
}

import { useState } from 'react'
import type { OrderResponse } from './api'
import { volumeFormat } from './format'

export default function ConfirmedVolumeCorrection({ order, maxCapacity, busy, onSave }: {
  order: OrderResponse
  maxCapacity: number
  busy: boolean
  onSave: (id: string, volume: number, expectedVolume: number) => Promise<void>
}) {
  const [draft, setDraft] = useState<{ expectedVolume: number; value: string } | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function save() {
    if (!draft) return
    const normalized = draft.value.trim().replace(',', '.')
    const volume = Number(normalized)
    if (!/^\d+(?:\.\d{1,3})?$/.test(normalized) || !Number.isFinite(volume) || volume <= 0) {
      setError('Introdu un volum pozitiv, cu cel mult 3 zecimale.')
      return
    }
    if (volume > maxCapacity) {
      setError(`Volumul trebuie să încapă într-un vehicul: maximum ${volumeFormat.format(maxCapacity)}.`)
      return
    }
    if (volume === draft.expectedVolume) {
      setError('Introdu un volum diferit de cel actual.')
      return
    }
    setError(null)
    try {
      await onSave(order.id, volume, draft.expectedVolume)
      setDraft(null)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Volumul nu a putut fi corectat.')
    }
  }

  if (!draft) return <button type="button" className="confirm-button" disabled={busy}
    onClick={() => { setDraft({ expectedVolume: order.volume, value: String(order.volume) }); setError(null) }}>
    Corectează volumul
  </button>

  return <div className="confirmed-volume-correction">
    <label>Volum corectat
      <input type="text" inputMode="decimal" value={draft.value} disabled={busy}
        aria-label={`Volum corectat pentru comanda ${order.address}`}
        onChange={event => { setDraft({ ...draft, value: event.target.value }); setError(null) }} />
    </label>
    <small>Volum citit: {volumeFormat.format(draft.expectedVolume)} · maxim vehicul: {volumeFormat.format(maxCapacity)}</small>
    <div className="confirmed-volume-actions">
      <button type="button" className="confirm-button" disabled={busy} onClick={() => { void save() }}>Salvează corectarea</button>
      <button type="button" className="secondary-button" disabled={busy} onClick={() => { setDraft(null); setError(null) }}>Renunță</button>
    </div>
    {error && <small className="volume-capacity-warning" role="alert">{error}</small>}
  </div>
}

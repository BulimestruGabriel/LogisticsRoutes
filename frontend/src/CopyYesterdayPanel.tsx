import { useState } from 'react'
import { ApiError, copyYesterday, previewYesterday,
  type CopyYesterdayPreview, type CopyYesterdayResult, type DeliveryStatus, type OrderStatus } from './api'
import { defaultCopySelection } from './copyYesterday'
import { volumeFormat } from './format'

const orderStatusLabels: Record<OrderStatus, string> = {
  New: 'Nouă', Confirmed: 'Confirmată', Planned: 'Planificată',
  Delivered: 'Livrată', Cancelled: 'Anulată',
}
const deliveryStatusLabels: Record<DeliveryStatus, string> = {
  Pending: 'în așteptare', Departed: 'în drum', Arrived: 'la destinație',
  Delivered: 'livrată', Refused: 'refuzată', PartialReturn: 'retur parțial',
}

function failure(error: unknown): string {
  if (error instanceof ApiError) return `Cererea a eșuat (${error.status}): ${error.message}`
  return 'Conexiunea cu API-ul a eșuat. Încearcă din nou.'
}

export default function CopyYesterdayPanel({ day, busy, onCopied, onPendingChange }: {
  day: string
  busy: boolean
  onCopied: (result: CopyYesterdayResult) => void
  onPendingChange: (pending: boolean) => void
}) {
  const [preview, setPreview] = useState<CopyYesterdayPreview | null>(null)
  const [selected, setSelected] = useState<string[]>([])
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<CopyYesterdayResult | null>(null)

  async function loadPreview() {
    if (pending || busy) return
    setPending(true)
    onPendingChange(true)
    setError(null)
    setResult(null)
    try {
      const loaded = await previewYesterday(day)
      setPreview(loaded)
      setSelected(defaultCopySelection(loaded.candidates))
    } catch (reason) {
      setError(failure(reason))
    } finally {
      setPending(false)
      onPendingChange(false)
    }
  }

  async function createCopies() {
    if (pending || busy || selected.length === 0) return
    setPending(true)
    onPendingChange(true)
    setError(null)
    try {
      const saved = await copyYesterday(day, selected)
      setResult(saved)
      onCopied(saved)
      try {
        const loaded = await previewYesterday(day)
        setPreview(loaded)
        setSelected(defaultCopySelection(loaded.candidates))
      } catch {
        setSelected([])
        setError('Comenzile au fost salvate, dar previzualizarea nu s-a putut reîncărca. Apasă Reîncarcă previzualizarea.')
      }
    } catch (reason) {
      setError(failure(reason))
      if (reason instanceof ApiError && reason.status === 409) setSelected([])
    } finally {
      setPending(false)
      onPendingChange(false)
    }
  }

  return <section className="panel copy-panel" aria-labelledby="copy-yesterday-title">
    <div className="panel-heading"><div><p className="section-kicker">PREGĂTIREA ZILEI</p>
      <h2 id="copy-yesterday-title">Copiază comenzile de ieri</h2></div></div>
    <p>Previzualizează comenzile, verifică eventualele comenzi de astăzi și alege ce copiezi ca New.</p>
    <button type="button" className="secondary-button" onClick={loadPreview} disabled={pending || busy}>
      {pending && !preview ? 'Se încarcă…' : preview ? 'Reîncarcă previzualizarea' : 'Previzualizează comenzile de ieri'}
    </button>
    {error && <div className="status-error" role="alert">{error}</div>}
    {result && <p role="status" className="copy-result">{result.created.length} comenzi New create; {result.alreadyCopied.length} existau deja din aceeași sursă.</p>}
    {preview && <div className="copy-preview">
      <p>Sursa: <strong>{preview.sourceDay}</strong> · Ziua aleasă: <strong>{preview.day}</strong>.</p>
      {preview.candidates.length === 0 ? <p>Nu există comenzi eligibile ieri.</p> : <>
        <div className="copy-candidates">{preview.candidates.map(candidate => {
          const source = candidate.sourceOrder
          const copied = !!candidate.alreadyCopiedOrderId
          return <label className={`copy-candidate${candidate.warnDeliveryOutcome ? ' has-warning' : ''}`} key={source.id}>
            <input type="checkbox" checked={selected.includes(source.id)} disabled={pending || busy || copied}
              onChange={event => setSelected(current => event.target.checked
                ? [...current, source.id] : current.filter(id => id !== source.id))} />
            <span><strong>{source.address}</strong> · {source.zone} · {volumeFormat.format(source.volume)} · {orderStatusLabels[source.status]}
              {candidate.deliveryStatus && <small>Oprire ieri: {deliveryStatusLabels[candidate.deliveryStatus]}</small>}
              {candidate.warnDeliveryOutcome && <small className="copy-warning">Atenție: oprirea a fost {candidate.deliveryStatus === 'Refused' ? 'refuzată' : 'cu retur parțial'}. Deselectată implicit; verifică înainte de copiere.</small>}
              {candidate.possibleExistingToday.length > 0 && <small className="copy-warning">Posibilă comandă existentă astăzi la aceeași zonă și adresă: {candidate.possibleExistingToday.map(order => `${order.address} (${orderStatusLabels[order.status]})`).join(', ')}. Verifică manual; adresa nu identifică sigur magazinul.</small>}
              {copied && <small>Deja copiată pentru această zi (ID {candidate.alreadyCopiedOrderId}).</small>}
            </span>
          </label>
        })}</div>
        <button type="button" className="primary-button" disabled={pending || busy || selected.length === 0}
          onClick={createCopies}>{pending ? 'Se salvează…' : `Creează ${selected.length} ${selected.length === 1 ? 'comandă New' : 'comenzi New'}`}</button>
      </>}
    </div>}
  </section>
}

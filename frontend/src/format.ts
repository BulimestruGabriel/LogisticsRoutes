import type { DeliveryStatus } from './api'

export const deliveryLabels: Record<DeliveryStatus, string> = {
  Pending: 'În așteptare',
  Departed: 'În drum',
  Arrived: 'La destinație',
  Delivered: 'Livrată',
  Refused: 'Refuzată',
  PartialReturn: 'Retur parțial',
}

export const numberFormat = new Intl.NumberFormat('ro-RO', { maximumFractionDigits: 2 })

import type { CopyYesterdayCandidate } from './api'

export function defaultCopySelection(candidates: CopyYesterdayCandidate[]): string[] {
  return candidates.filter(candidate => !candidate.warnDeliveryOutcome && !candidate.alreadyCopiedOrderId)
    .map(candidate => candidate.sourceOrder.id)
}

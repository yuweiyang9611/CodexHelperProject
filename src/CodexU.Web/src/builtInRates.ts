import type { ModelCreditRate } from './types'

/**
 * Helpers for seeding the rate editor from the built-in catalog.
 *
 * The catalog is append-only: a model keeps every rate it has ever had, one row
 * per price change, so that historical months replay at the price that applied
 * on the day. That makes "the rate for this model" ambiguous unless a date is
 * chosen, which is what these functions settle.
 */

/**
 * The built-in rate in force for a model today.
 *
 * Rows with no effective date apply to all history and are the oldest thing
 * there is, so a dated row always wins over them — Sonnet 5's introductory rate
 * is undated and its standard rate starts 2026-09-01, and defaulting a new
 * override to the introductory figure would quietly underprice every future
 * month.
 */
export function newestBuiltInRateFor(
  rates: ModelCreditRate[] | undefined,
  model: string,
): ModelCreditRate | null {
  const wanted = model.trim().toLowerCase()
  if (!wanted) return null
  const matches = (rates ?? []).filter(rate => rate.model.trim().toLowerCase() === wanted)
  if (matches.length === 0) return null
  return matches.reduce((newest, rate) =>
    (rate.effectiveFrom ?? '') > (newest.effectiveFrom ?? '') ? rate : newest)
}

/** Every model the built-in catalog prices, deduplicated and ordered. */
export function builtInModelNames(rates: ModelCreditRate[] | undefined): string[] {
  return [...new Set((rates ?? []).map(rate => rate.model))].sort()
}

/**
 * The model to seed a new override row with: the first priced model the user has
 * not already overridden. Offering one that is already in the table would create
 * a duplicate the user has to notice and fix.
 */
export function nextUnoverriddenModel(
  rates: ModelCreditRate[] | undefined,
  existing: ModelCreditRate[],
): string {
  const overridden = new Set(existing.map(rate => rate.model.trim().toLowerCase()))
  return builtInModelNames(rates).find(name => !overridden.has(name.trim().toLowerCase())) ?? ''
}

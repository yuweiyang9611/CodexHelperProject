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
  effectiveOn = localDate(new Date()),
): ModelCreditRate | null {
  const wanted = normalizeRateModel(model)
  if (!wanted) return null
  const matches = (rates ?? []).filter(rate =>
    normalizeRateModel(rate.model) === wanted
    && (!rate.effectiveFrom || rate.effectiveFrom <= effectiveOn))
  if (matches.length === 0) return null
  return matches.reduce((newest, rate) =>
    (rate.effectiveFrom ?? '') > (newest.effectiveFrom ?? '') ? rate : newest)
}

/** Mirrors the backend's exact aliases and dated-snapshot normalization. */
export function normalizeRateModel(model: string): string {
  return normalizeRatePattern(model, 'exact')
}

/** Keeps literal family prefixes intact while exact keys resolve official aliases. */
export function normalizeRatePattern(model: string, matchMode: string | null | undefined): string {
  let normalized = model.trim().toLowerCase().replaceAll('_', '-').replaceAll(' ', '-')
  if (normalized.endsWith('-latest')) normalized = normalized.slice(0, -'-latest'.length)
  normalized = stripDatedSnapshotSuffix(normalized)
  if (matchMode?.toLowerCase() === 'prefix') return normalized
  if (normalized === 'gpt-5.2-codex') return 'gpt-5.2'
  if (normalized === 'gpt-5.6') return 'gpt-5.6-sol'
  return normalized
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
  return builtInModelNames(rates).find((name) => {
    const candidate = normalizeRateModel(name)
    return !existing.some((rate) => {
      const pattern = normalizeRatePattern(rate.model, rate.matchMode)
      return candidate === pattern
        || rate.matchMode?.toLowerCase() === 'prefix'
          && candidate.startsWith(`${pattern}-`)
    })
  }) ?? ''
}

function localDate(date: Date): string {
  return [
    date.getFullYear(),
    String(date.getMonth() + 1).padStart(2, '0'),
    String(date.getDate()).padStart(2, '0'),
  ].join('-')
}

function stripDatedSnapshotSuffix(model: string): string {
  const dashed = /-(\d{4})-(\d{2})-(\d{2})$/.exec(model)
  if (dashed?.index != null && isSnapshotDate(dashed[1], dashed[2], dashed[3])) {
    return model.slice(0, dashed.index)
  }

  const compact = /-(\d{4})(\d{2})(\d{2})$/.exec(model)
  if (compact?.index != null && isSnapshotDate(compact[1], compact[2], compact[3])) {
    return model.slice(0, compact.index)
  }

  return model
}

function isSnapshotDate(yearText: string, monthText: string, dayText: string): boolean {
  const year = Number(yearText)
  const month = Number(monthText)
  const day = Number(dayText)
  if (year < 2000 || month < 1 || month > 12 || day < 1) return false
  return day <= new Date(Date.UTC(year, month, 0)).getUTCDate()
}

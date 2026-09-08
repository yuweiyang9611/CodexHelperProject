import { describe, expect, it } from 'vitest'
import {
  builtInModelNames,
  newestBuiltInRateFor,
  nextUnoverriddenModel,
  normalizeRateModel,
  normalizeRatePattern,
} from '../../src/builtInRates'
import type { ModelCreditRate } from '../../src/types'

function rate(overrides: Partial<ModelCreditRate> & { model: string }): ModelCreditRate {
  return {
    inputCreditsPerMillion: 0,
    cachedInputCreditsPerMillion: 0,
    outputCreditsPerMillion: 0,
    effectiveFrom: null,
    ...overrides,
  }
}

// The real catalog shape: Sonnet 5 carries an undated introductory row and a
// dated standard row that supersedes it.
const CATALOG: ModelCreditRate[] = [
  rate({ model: 'claude-opus-5', inputCreditsPerMillion: 125, cachedInputCreditsPerMillion: 12.5, outputCreditsPerMillion: 625 }),
  rate({ model: 'claude-sonnet-5', inputCreditsPerMillion: 50, cachedInputCreditsPerMillion: 5, outputCreditsPerMillion: 250 }),
  rate({ model: 'claude-sonnet-5', inputCreditsPerMillion: 75, cachedInputCreditsPerMillion: 7.5, outputCreditsPerMillion: 375, effectiveFrom: '2026-09-01' }),
  rate({ model: 'gpt-5.6-sol', inputCreditsPerMillion: 125, cachedInputCreditsPerMillion: 12.5, outputCreditsPerMillion: 750 }),
  rate({ model: 'gpt-5.6-sol', inputCreditsPerMillion: 100, cachedInputCreditsPerMillion: 10, outputCreditsPerMillion: 500, effectiveFrom: '2026-08-21' }),
  rate({ model: 'gpt-5.2', inputCreditsPerMillion: 43.75, cachedInputCreditsPerMillion: 4.375, outputCreditsPerMillion: 350 }),
]

describe('newestBuiltInRateFor', () => {
  it('picks the dated row over the undated one it supersedes', () => {
    // Sonnet 5's introductory rate is undated and its standard rate starts
    // 2026-09-01. Seeding a new override from the introductory figure would
    // quietly underprice every month from then on.
    expect(newestBuiltInRateFor(CATALOG, 'claude-sonnet-5')?.inputCreditsPerMillion).toBe(75)
    expect(newestBuiltInRateFor(CATALOG, 'claude-sonnet-5')?.outputCreditsPerMillion).toBe(375)
  })

  it('returns the only row when a model has just one', () => {
    const found = newestBuiltInRateFor(CATALOG, 'claude-opus-5')
    expect(found?.inputCreditsPerMillion).toBe(125)
    expect(found?.cachedInputCreditsPerMillion).toBe(12.5)
    expect(found?.outputCreditsPerMillion).toBe(625)
  })

  it('matches regardless of surrounding whitespace or case', () => {
    expect(newestBuiltInRateFor(CATALOG, '  Claude-Opus-5 ')?.inputCreditsPerMillion).toBe(125)
  })

  it('says nothing rather than guessing for an unknown or blank model', () => {
    // An override for a model the catalog has never priced is the main reason to
    // add a row by hand — it must start blank rather than borrowing someone
    // else's numbers.
    expect(newestBuiltInRateFor(CATALOG, 'some-unreleased-model')).toBeNull()
    expect(newestBuiltInRateFor(CATALOG, '   ')).toBeNull()
    expect(newestBuiltInRateFor(undefined, 'claude-opus-5')).toBeNull()
  })

  it('resolves aliases and snapshots using the same rules as the backend', () => {
    expect(newestBuiltInRateFor(CATALOG, 'gpt-5.6', '2026-09-09')?.inputCreditsPerMillion).toBe(100)
    expect(newestBuiltInRateFor(CATALOG, 'gpt-5.6-sol-2026-07-09', '2026-09-09')?.outputCreditsPerMillion).toBe(500)

    const daybreak = [
      rate({ model: 'gpt-daybreak-blue', inputCreditsPerMillion: 125, effectiveFrom: '2026-08-07' }),
      rate({ model: 'gpt-daybreak-blue', inputCreditsPerMillion: 100, effectiveFrom: '2026-08-21' }),
    ]
    expect(newestBuiltInRateFor(daybreak, 'gpt-daybreak-blue-latest', '2026-09-09')?.inputCreditsPerMillion).toBe(100)
  })

  it('does not seed an override from a future price version', () => {
    const withFuture = [
      ...CATALOG,
      rate({ model: 'gpt-5.6-sol', inputCreditsPerMillion: 999, effectiveFrom: '2026-11-22' }),
    ]

    expect(newestBuiltInRateFor(withFuture, 'gpt-5.6-sol', '2026-09-09')?.inputCreditsPerMillion).toBe(100)
  })
})

describe('normalizeRateModel', () => {
  it.each([
    ['gpt-5.2-codex-latest', 'gpt-5.2'],
    ['gpt-5.6-latest', 'gpt-5.6-sol'],
    ['gpt-daybreak-blue-latest', 'gpt-daybreak-blue'],
    ['gpt-daybreak-red-latest', 'gpt-daybreak-red'],
    ['gpt-6-astra-2026-09-03', 'gpt-6-astra'],
    ['claude-opus-5-20260514', 'claude-opus-5'],
  ])('normalizes %s to %s', (model, expected) => {
    expect(normalizeRateModel(model)).toBe(expected)
  })

  it('keeps family semantics for prefix rate patterns', () => {
    expect(normalizeRatePattern(' GPT_5.6 ', 'prefix')).toBe('gpt-5.6')
    expect(normalizeRatePattern('gpt-5.6', 'exact')).toBe('gpt-5.6-sol')
  })

  it('keeps an invalid date-like suffix', () => {
    expect(normalizeRateModel('gpt-6-astra-2026-02-30')).toBe('gpt-6-astra-2026-02-30')
  })
})

describe('builtInModelNames', () => {
  it('lists each model once, in order', () => {
    expect(builtInModelNames(CATALOG)).toEqual([
      'claude-opus-5',
      'claude-sonnet-5',
      'gpt-5.2',
      'gpt-5.6-sol',
    ])
  })

  it('is empty when the catalog has not loaded', () => {
    expect(builtInModelNames(undefined)).toEqual([])
  })
})

describe('nextUnoverriddenModel', () => {
  it('skips models the user has already overridden', () => {
    // Offering one that is already in the table would create a duplicate row the
    // user has to spot and remove.
    const existing = [rate({ model: 'claude-opus-5' })]

    expect(nextUnoverriddenModel(CATALOG, existing)).toBe('claude-sonnet-5')
  })

  it('falls back to a blank model once every priced model is overridden', () => {
    const existing = builtInModelNames(CATALOG).map(model => rate({ model }))

    expect(nextUnoverriddenModel(CATALOG, existing)).toBe('')
  })

  it('ignores case when deciding what is already overridden', () => {
    const existing = [rate({ model: 'CLAUDE-OPUS-5' })]

    expect(nextUnoverriddenModel(CATALOG, existing)).toBe('claude-sonnet-5')
  })

  it('treats an official alias as overriding its concrete model', () => {
    const existing = [
      rate({ model: 'claude-opus-5' }),
      rate({ model: 'claude-sonnet-5' }),
      rate({ model: 'gpt-5.2' }),
      rate({ model: 'gpt-5.6' }),
    ]

    expect(nextUnoverriddenModel(CATALOG, existing)).toBe('')
  })

  it('treats a family prefix as overriding every matching built-in model', () => {
    const catalog = [
      rate({ model: 'gpt-5.6-sol' }),
      rate({ model: 'gpt-5.6-terra' }),
      rate({ model: 'gpt-5.6-cyber' }),
    ]

    expect(nextUnoverriddenModel(catalog, [rate({ model: 'gpt-5.6', matchMode: 'prefix' })])).toBe('')
  })
})

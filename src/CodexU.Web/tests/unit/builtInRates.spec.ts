import { describe, expect, it } from 'vitest'
import { builtInModelNames, newestBuiltInRateFor, nextUnoverriddenModel } from '../../src/builtInRates'
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
})

describe('builtInModelNames', () => {
  it('lists each model once, in order', () => {
    expect(builtInModelNames(CATALOG)).toEqual([
      'claude-opus-5',
      'claude-sonnet-5',
      'gpt-5.2',
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
})

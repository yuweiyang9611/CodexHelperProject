import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { appSettings } from './fixtures'

vi.mock('../../src/host', () => ({
  host: { request: vi.fn(), on: vi.fn() },
}))

const { useDashboardStore } = await import('../../src/stores/dashboard')
const { useViewportScale } = await import('../../src/composables/useViewportScale')

const originalInnerWidth = window.innerWidth

beforeEach(() => {
  setActivePinia(createPinia())
  Object.defineProperty(window, 'innerWidth', { configurable: true, value: 360 })
})

afterEach(() => {
  Object.defineProperty(window, 'innerWidth', { configurable: true, value: originalInnerWidth })
})

describe('useViewportScale', () => {
  it('uses the explicit preference without shrinking it for a narrow viewport', () => {
    const store = useDashboardStore()
    store.settings = appSettings({ uiScalePercent: 140 })
    const { uiScale, layoutStyle } = useViewportScale()

    expect(uiScale.value).toBe(1.4)
    expect(layoutStyle.value.zoom).toBe(1.4)
    expect(layoutStyle.value.width).toBe(`${100 / 1.4}%`)

    window.dispatchEvent(new Event('resize'))

    expect(uiScale.value).toBe(1.4)
    expect(layoutStyle.value.zoom).toBe(1.4)
  })

  it('clamps host-provided values to the supported 90–140 percent range', () => {
    const store = useDashboardStore()
    store.settings = appSettings({ uiScalePercent: 10 })
    const { uiScale } = useViewportScale()
    expect(uiScale.value).toBe(.9)

    store.settings!.uiScalePercent = 999
    expect(uiScale.value).toBe(1.4)
  })
})

import { createApp, nextTick, ref, type App } from 'vue'
import { afterEach, expect, it, vi } from 'vitest'
import { antigravityQuotaVisible, useAntigravityQuota } from '../../src/composables/useAntigravityQuota'
import type { AntigravityQuotaSnapshot } from '../../src/types'

const { request } = vi.hoisted(() => ({ request: vi.fn() }))
vi.mock('../../src/host', () => ({ host: { request } }))
let app: App | undefined

afterEach(() => {
  app?.unmount()
  app = undefined
  vi.restoreAllMocks()
  vi.useRealTimers()
  request.mockReset()
})

it('refreshes only while active and visible, coalesces pending reads, and resumes when due', async () => {
  vi.useFakeTimers()
  let hidden = false
  vi.spyOn(document, 'hidden', 'get').mockImplementation(() => hidden)
  let complete!: (value: AntigravityQuotaSnapshot) => void
  request.mockReturnValueOnce(new Promise(resolve => { complete = resolve }))
  request.mockResolvedValue({ status: 'noQuota', groups: [] })
  const active = ref(false)
  const refreshMinutes = ref(2)
  let model!: ReturnType<typeof useAntigravityQuota>
  app = createApp({
    setup() {
      model = useAntigravityQuota(active, refreshMinutes)
      return () => null
    },
  })
  app.mount(document.createElement('div'))

  expect(request).not.toHaveBeenCalled()
  active.value = true
  await nextTick()
  expect(request).toHaveBeenCalledExactlyOnceWith('antigravity.getQuota')
  await model.refresh()
  await vi.advanceTimersByTimeAsync(120_000)
  expect(request).toHaveBeenCalledTimes(1)

  complete({ status: 'ready', groups: [] })
  await Promise.resolve()
  expect(model.quota.value?.status).toBe('ready')
  await vi.advanceTimersByTimeAsync(119_999)
  expect(request).toHaveBeenCalledTimes(1)
  await vi.advanceTimersByTimeAsync(1)
  expect(request).toHaveBeenCalledTimes(2)

  hidden = true
  document.dispatchEvent(new Event('visibilitychange'))
  await vi.advanceTimersByTimeAsync(120_000)
  expect(request).toHaveBeenCalledTimes(2)
  hidden = false
  document.dispatchEvent(new Event('visibilitychange'))
  await Promise.resolve()
  expect(request).toHaveBeenCalledTimes(3)

  active.value = false
  await nextTick()
  await vi.advanceTimersByTimeAsync(120_000)
  expect(request).toHaveBeenCalledTimes(3)
})

it.each([
  [true, false, 'antigravity', true],
  [true, true, 'diagnostics', true],
  [true, false, 'combined', true],
  [false, false, 'combined', true],
  [true, false, 'diagnostics', false],
  [false, false, 'usage', false],
  [false, true, 'combined', false],
])('polls only while an Antigravity view is shown (selected %s, compact %s, tab %s)', (antigravitySelected, compactMode, activeTab, expected) => {
  expect(antigravityQuotaVisible({ antigravitySelected, compactMode, activeTab })).toBe(expected)
})

it('runs an explicit refresh while no view polls, without starting a schedule', async () => {
  vi.useFakeTimers()
  request.mockResolvedValue({ status: 'noQuota', groups: [] })
  const active = ref(false)
  let model!: ReturnType<typeof useAntigravityQuota>
  app = createApp({
    setup() {
      model = useAntigravityQuota(active, ref(1))
      return () => null
    },
  })
  app.mount(document.createElement('div'))

  await model.refresh()
  expect(request).not.toHaveBeenCalled()
  await model.refresh(true)
  expect(request).toHaveBeenCalledOnce()
  await vi.advanceTimersByTimeAsync(5 * 60_000)
  expect(request).toHaveBeenCalledOnce()
})

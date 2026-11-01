import { createApp, type App } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'
import QuotaRing from '../../src/components/QuotaRing.vue'
import { quotaForecast, rateLimitWindow } from './fixtures'

let app: App | null = null

afterEach(() => {
  app?.unmount()
  app = null
  document.body.replaceChildren()
  vi.useRealTimers()
})

describe('quota source presentation', () => {
  it('keeps a normal quota with a known reset on the existing countdown layout', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-11-01T09:40:00Z'))
    const container = document.createElement('div')
    document.body.append(container)
    app = createApp(QuotaRing, {
      label: '5 小时', color: 'blue',
      quota: rateLimitWindow({ resetsAt: '2026-11-01T10:40:00Z', measuredAt: '2026-11-01T09:35:00Z' }),
    })
    app.mount(container)

    expect(container.querySelector('.quota-caption')?.textContent).toBe('剩余')
    expect(container.querySelector('.reset-time')?.textContent).toBe('1 小时后重置')
    expect(container.querySelector('.sample-time')).toBeNull()
    expect(container.querySelector('.sampled')).toBeNull()
  })

  it('labels Desktop history as a dated sample and never invents a reset or forecast countdown', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-11-01T09:40:00Z'))
    const container = document.createElement('div')
    document.body.append(container)
    app = createApp(QuotaRing, {
      label: '5 小时', color: 'blue',
      quota: rateLimitWindow({
        source: 'claude-desktop-history', remainingPercent: 73, usedPercent: 27,
        measuredAt: '2026-11-01T09:35:00Z', resetsAt: undefined,
      }),
      forecast: quotaForecast({ exhaustsBeforeReset: true, exhaustsAt: '2026-11-01T10:00:00Z' }),
    })
    app.mount(container)

    expect(container.querySelector('.quota-caption')?.textContent).toBe('采样剩余')
    expect(container.querySelector('strong')?.textContent).toBe('73%')
    expect(container.textContent).toContain('2026/11/01 18:35:00')
    expect(container.textContent).toContain('本地时间 · 非实时')
    expect(container.textContent).toContain('来源未提供重置时间')
    expect(container.querySelector('time')?.getAttribute('datetime')).toBe('2026-11-01T09:35:00Z')
    expect(container.querySelector('.burn-down')).toBeNull()
    expect(container.textContent).not.toContain('后重置')
    expect(container.querySelector('svg')?.getAttribute('aria-label')).toContain('采样剩余 73%')
  })

  it('does not replace a missing sample timestamp with the refresh time', () => {
    const container = document.createElement('div')
    document.body.append(container)
    app = createApp(QuotaRing, {
      label: '7 天', color: 'violet',
      quota: rateLimitWindow({ source: 'claude-desktop-history', measuredAt: null, resetsAt: null }),
    })
    app.mount(container)

    expect(container.textContent).toContain('采样时间不可用')
    expect(container.querySelector('time')?.hasAttribute('datetime')).toBe(false)
    expect(container.textContent).not.toContain('Invalid Date')
  })
})

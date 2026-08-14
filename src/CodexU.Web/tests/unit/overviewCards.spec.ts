import { createPinia, setActivePinia, type Pinia } from 'pinia'
import { createApp, type App } from 'vue'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import OverviewCards from '../../src/components/OverviewCards.vue'
import { snapshot, tokenPeriod } from './fixtures'

let pinia: Pinia
let app: App | null

beforeEach(() => {
  pinia = createPinia()
  setActivePinia(pinia)
  app = null
})

afterEach(() => {
  app?.unmount()
  document.body.replaceChildren()
})

function mountOverview(overviewSnapshot: ReturnType<typeof snapshot>) {
  const container = document.createElement('div')
  document.body.append(container)
  app = createApp(OverviewCards, { snapshot: overviewSnapshot })
  app.use(pinia)
  app.mount(container)
  return container
}

describe('OverviewCards local token activity', () => {
  it('uses the local JSONL periods for every token metric', () => {
    const container = mountOverview(snapshot({
      tokens: {
        today: tokenPeriod({ tokens: 2_480_545_769 }),
        sevenDays: tokenPeriod({ tokens: 2_900_000_000 }),
        month: tokenPeriod({ tokens: 3_100_000_000 }),
        lifetime: tokenPeriod({ tokens: 3_758_082_435 }),
      },
    }))

    const tokenCard = container.querySelector('.token-card')
    expect(tokenCard?.textContent).toContain('本机原始统计')
    expect(tokenCard?.textContent).toContain('今日2.48B')
    expect(tokenCard?.textContent).toContain('近 7 天2.90B')
    expect(tokenCard?.textContent).toContain('本月3.10B')
    expect(tokenCard?.textContent).toContain('累计3.76B')
    expect(tokenCard?.textContent).toContain('来自本机日志原始事件')
    expect(tokenCard?.textContent).not.toContain('官方账户统计')
  })
})

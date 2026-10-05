import { createApp, nextTick, type App } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'
import SurfaceView from '../../src/views/SurfaceView.vue'

type SurfaceData = Parameters<Parameters<NonNullable<typeof window.codexUSurface>['subscribe']>[0]>[0]
let app: App | undefined

afterEach(() => {
  app?.unmount()
  app = undefined
  delete window.codexUSurface
  document.body.replaceChildren()
})

async function mountSurface(overrides: Partial<SurfaceData> = {}) {
  const action = vi.fn().mockResolvedValue(true)
  const unsubscribe = vi.fn()
  let emit!: (data: SurfaceData) => void
  const data: SurfaceData = {
    expanded: true,
    statusStripShowTodayTokens: true,
    todayAmount: 12.34,
    presentation: {
      runtimeTitle: 'Claude Code 使用状态', primaryLabel: '5h 剩余', secondaryLabel: '7d 剩余',
      primaryQuota: { text: '75%', accessibleText: '5 小时剩余额度 75%', resetText: '9/22 18:00 刷新' },
      secondaryQuota: { text: '--', accessibleText: '7 天额度不可用' },
      today: { text: '99M', accessibleText: '今日 99M Token' },
      sevenDays: { text: '999M', accessibleText: '近 7 天 999M Token' },
      lifetime: { text: '9.99B', accessibleText: '累计 9.99B Token' },
      statusText: '额度更新于 15:00', statusToolTip: '账户额度独立于本机用量',
    },
    ...overrides,
  }
  window.codexUSurface = { action, subscribe(listener) {
    emit = listener
    listener(data)
    return unsubscribe
  } }
  const container = document.createElement('div')
  document.body.append(container)
  app = createApp(SurfaceView)
  app.mount(container)
  await nextTick()
  return { container, action, unsubscribe, emit, data }
}

function button(container: ParentNode, text: string): HTMLButtonElement {
  const match = [...container.querySelectorAll<HTMLButtonElement>('button')]
    .find(candidate => candidate.textContent?.includes(text))
  if (!match) throw new Error(`Missing button ${text}`)
  return match
}

describe('restored rich status strip', () => {
  it('shows quota, local token periods and today amount, and releases the subscription', async () => {
    const { container, action, unsubscribe } = await mountSurface()
    expect(container.textContent).toContain('5h 75%')
    expect(container.querySelector('header span')?.getAttribute('title')).toBe('9/22 18:00 刷新')
    expect(container.textContent).toContain('今日 Token 99M')
    expect(container.textContent).toContain('近 7 天 999M')
    expect(container.textContent).toContain('累计 9.99B')
    expect(container.textContent).toContain('今日等效金额 US$12.34')
    expect(action).toHaveBeenCalledWith('ready')
    button(container, '打开主界面').click()
    expect(action).toHaveBeenCalledWith('open')
    app?.unmount()
    app = undefined
    expect(unsubscribe).toHaveBeenCalledOnce()
  })

  it('honors the today-token preference and live expansion/position updates', async () => {
    const { container, action, emit, data } = await mountSurface({ statusStripShowTodayTokens: false })
    expect(container.textContent).not.toContain('今日 Token')
    expect(container.textContent).toContain('近 7 天')
    button(container, '锁定位置').click()
    expect(action).toHaveBeenCalledWith('lock')
    emit({ ...data, expanded: false, statusStripPositionLocked: true })
    await nextTick()
    expect(container.querySelector('section')).toBeNull()
    expect(container.querySelector('header')?.classList.contains('locked')).toBe(true)
    button(container, '展开').click()
    expect(action).toHaveBeenCalledWith('expand')
  })

  it('keeps refresh recovery and host errors visible', async () => {
    const { container, action, emit, data } = await mountSurface({ refreshing: true })
    expect(button(container, '刷新中').disabled).toBe(true)
    emit({ ...data, refreshing: false, refreshError: '本机读取失败，请重试' })
    await nextTick()
    expect(container.textContent).toContain('本机读取失败，请重试')
    action.mockRejectedValueOnce(new Error('连接断开'))
    button(container, '刷新').click()
    await vi.waitFor(() => expect(container.querySelector('[role="alert"]')?.textContent).toContain('连接断开'))
    expect(button(container, '刷新').disabled).toBe(false)
  })
})

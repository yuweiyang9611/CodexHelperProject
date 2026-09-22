import { createApp, nextTick, type App } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'
import SurfaceView from '../../src/views/SurfaceView.vue'

let app: App | undefined

afterEach(() => {
  app?.unmount()
  app = undefined
  delete window.codexUSurface
  document.body.replaceChildren()
})

describe('quota-only status strip', () => {
  it('shows remaining quotas and reset times without rendering retained token or task fields', async () => {
    const action = vi.fn().mockResolvedValue(true)
    const unsubscribe = vi.fn()
    window.codexUSurface = { action, subscribe(listener) {
      listener({ expanded: true, presentation: {
        runtimeTitle: 'Claude Code 使用状态', primaryLabel: '5h 剩余', secondaryLabel: '7d 剩余',
        primaryQuota: { text: '75%', accessibleText: '5 小时剩余额度 75%', resetText: '9/22 18:00 刷新' },
        secondaryQuota: { text: '--', accessibleText: '7 天额度不可用' },
        statusText: '额度更新于 15:00', statusToolTip: '账户额度独立于本机用量',
        today: { text: '99M' }, sevenDays: { text: '999M' }, lifetime: { text: '9999M' }, todoText: '22',
      } } as Parameters<typeof listener>[0])
      return unsubscribe
    } }
    const container = document.createElement('div')
    document.body.append(container)
    app = createApp(SurfaceView)
    app.mount(container)
    await nextTick()
    expect(container.textContent).toContain('5h 剩余 75%')
    expect(container.textContent).toContain('9/22 18:00 刷新')
    expect(container.textContent).toContain('刷新时间未知')
    expect(container.textContent).not.toMatch(/Token|今日|累计|等效金额|待办|99M/)
    expect(action).toHaveBeenCalledWith('ready')
    const open = [...container.querySelectorAll('button')].find(button => button.textContent === '打开主界面')!
    open.click()
    expect(action).toHaveBeenCalledWith('open')
    app.unmount()
    app = undefined
    expect(unsubscribe).toHaveBeenCalledOnce()
  })
})

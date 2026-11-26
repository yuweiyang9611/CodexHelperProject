import { createApp, nextTick, type App } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ClaudeCliAuthStatus from '../../src/components/ClaudeCliAuthStatus.vue'
import type { ClaudeCliAuthStatus as AuthResult, ClaudeCliQuotaSchedule } from '../../src/types'

const { request, handlers, on } = vi.hoisted(() => {
  const handlers = new Map<string, Set<() => void>>()
  const on = vi.fn((method: string, handler: () => void) => {
    const set = handlers.get(method) ?? new Set()
    set.add(handler)
    handlers.set(method, set)
    return () => set.delete(handler)
  })
  return { request: vi.fn(), handlers, on }
})
vi.mock('../../src/host', () => ({ host: { request, on } }))
const emit = (method: string) => handlers.get(method)?.forEach(handler => handler())
let app: App | undefined
const result = (overrides: Partial<AuthResult> = {}): AuthResult => ({
  status: 'loggedOut', authMethod: 'none', source: 'desktopEmbedded', checkedAt: '2026-11-25T10:30:00Z', ...overrides,
})

// A fixed local midday, so "today" in the expectations never crosses midnight mid-run.
const testNow = new Date(2026, 10, 25, 12, 0)
beforeEach(() => {
  vi.useFakeTimers({ toFake: ['Date'], now: testNow })
  request.mockReset()
  handlers.clear()
})
afterEach(() => {
  app?.unmount()
  app = undefined
  document.body.replaceChildren()
  vi.useRealTimers()
})

function mount() {
  const container = document.createElement('div')
  document.body.append(container)
  app = createApp(ClaudeCliAuthStatus)
  app.mount(container)
  return container
}

describe('Claude CLI auth status', () => {
  it('checks once on mount, disables concurrent checks, and supports refreshing after sign-in', async () => {
    let finish!: (value: AuthResult) => void
    request.mockReturnValueOnce(new Promise(resolve => { finish = resolve }))
    const container = mount()
    await nextTick()
    const button = container.querySelector('button')!
    expect(container.querySelector('[role="status"]')?.textContent).toBe('正在检查…')
    expect(button.disabled).toBe(true)
    button.click()
    expect(request).toHaveBeenCalledTimes(1)
    expect(request).toHaveBeenCalledWith('claude.getAuthStatus')
    finish(result())
    await vi.waitFor(() => expect(container.querySelector('[role="status"]')?.textContent).toBe('未登录'))
    expect(container.textContent).toContain('与 Desktop 相同的账号')
    expect(container.textContent).toContain('检测来源 Desktop 内嵌 CLI')
    expect(container.querySelector('time')?.dateTime).toBe('2026-11-25T10:30:00Z')
    request.mockResolvedValueOnce(result({ status: 'loggedIn', authMethod: 'claudeAi' }))
    button.click()
    await vi.waitFor(() => expect(container.querySelector('[role="status"]')?.textContent).toBe('已登录'))
    expect(container.textContent).toContain('Claude 订阅账号')
    expect(container.textContent).toContain('Desktop 内嵌 CLI')
    expect(request.mock.calls).toEqual([
      ['claude.getAuthStatus'], ['claude.getCliQuotaStatus'],
      ['claude.getAuthStatus'], ['claude.getCliQuotaStatus'],
    ])
  })

  it.each([
    ['apiKey', 'API 密钥'], ['oauthToken', 'OAuth 令牌'], ['thirdParty', '第三方服务'],
  ] as const)('labels %s login independently from a subscription account', async (authMethod, label) => {
    request.mockResolvedValueOnce(result({ status: 'loggedIn', authMethod, source: 'standalone' }))
    const container = mount()
    await vi.waitFor(() => expect(container.textContent).toContain(label))
    expect(container.textContent).toContain('独立安装的 CLI')
    expect(container.textContent).not.toContain('Claude 订阅账号')
  })

  it('shows unavailable safely when the request fails and can retry', async () => {
    request.mockRejectedValueOnce(new Error('private-account secret-token C:\\Private'))
    const container = mount()
    await vi.waitFor(() => expect(container.textContent).toContain('暂时无法检查'))
    expect(container.textContent).not.toContain('secret-token')
    expect(container.textContent).not.toContain('private-account')
    expect(container.textContent).not.toContain('Private')
    request.mockResolvedValueOnce(result({ status: 'notInstalled', source: 'none' }))
    container.querySelector('button')!.click()
    await vi.waitFor(() => expect(container.textContent).toContain('未找到 CLI'))
    expect(container.textContent).toContain('请安装 Claude Code CLI')
  })

  const today = (hours: number, minutes: number) => {
    const date = new Date(testNow)
    date.setHours(hours, minutes, 0, 0)
    return date.toISOString()
  }
  const lastSuccessAt = today(10, 30)
  const nextAttemptAt = today(10, 35)
  const time = (value: string) => new Date(value).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
  it.each([
    [{ enabled: true, lastStatus: 'fresh', lastSuccessAt, nextAttemptAt },
      `后台额度查询：最近一次成功读取于 ${time(lastSuccessAt)}，${time(nextAttemptAt)} 后再查询。`],
    [{ enabled: true, lastStatus: 'notAvailable' },
      '后台额度查询：CLI 当前账号没有订阅额度（未登录、API 密钥或第三方服务），下次刷新时查询；登录后点击“重新检查登录状态”可立即恢复。'],
    [{ enabled: true, lastStatus: 'unavailable', lastSuccessAt, nextAttemptAt },
      `后台额度查询失败（可能被用量接口限流），${time(nextAttemptAt)} 后再查询；上次成功读取于 ${time(lastSuccessAt)}。`],
    [{ enabled: false, lastStatus: 'fresh', lastSuccessAt }, '后台额度查询已关闭。'],
  ] satisfies [ClaudeCliQuotaSchedule, string][])('shows the quota schedule for %j', async (schedule, expected) => {
    request.mockResolvedValueOnce(result()).mockResolvedValueOnce(schedule)
    const container = mount()
    await vi.waitFor(() => expect(container.querySelector('.cli-quota-schedule')?.textContent).toBe(expected))
  })

  it('dates a saved time from another day', async () => {
    const yesterday = new Date(testNow)
    yesterday.setDate(yesterday.getDate() - 1)
    yesterday.setHours(10, 30, 0, 0)
    request.mockResolvedValueOnce(result()).mockResolvedValueOnce(
      { enabled: true, lastStatus: 'unavailable', lastSuccessAt: yesterday.toISOString(), nextAttemptAt })
    const container = mount()
    const day = yesterday.toLocaleDateString([], { month: 'numeric', day: 'numeric' })
    await vi.waitFor(() => expect(container.querySelector('.cli-quota-schedule')?.textContent)
      .toContain(`上次成功读取于 ${day} ${time(yesterday.toISOString())}`))
    expect(container.querySelector('.cli-quota-schedule')?.textContent).toContain(`，${time(nextAttemptAt)} 后再查询`)
  })

  it('reloads the schedule when settings or the usage snapshot change, and stops after unmount', async () => {
    request.mockResolvedValueOnce(result()).mockResolvedValueOnce({ enabled: true, lastStatus: 'fresh', lastSuccessAt })
    const container = mount()
    await vi.waitFor(() => expect(container.querySelector('.cli-quota-schedule')?.textContent).toContain('最近一次成功读取'))

    request.mockResolvedValueOnce({ enabled: false })
    emit('settings.changed')
    await vi.waitFor(() => expect(container.querySelector('.cli-quota-schedule')?.textContent).toBe('后台额度查询已关闭。'))

    request.mockResolvedValueOnce({ enabled: true, lastStatus: 'unavailable' })
    emit('usage.snapshotChanged')
    await vi.waitFor(() => expect(container.querySelector('.cli-quota-schedule')?.textContent).toContain('查询失败'))

    app?.unmount()
    app = undefined
    emit('settings.changed')
    expect(request).toHaveBeenCalledTimes(4)
  })

  it('does not poll or request settings mutations', async () => {
    vi.useFakeTimers()
    request.mockResolvedValueOnce(result())
    mount()
    await nextTick()
    await vi.advanceTimersByTimeAsync(60_000)
    expect(request.mock.calls).toEqual([['claude.getAuthStatus'], ['claude.getCliQuotaStatus']])
  })
})

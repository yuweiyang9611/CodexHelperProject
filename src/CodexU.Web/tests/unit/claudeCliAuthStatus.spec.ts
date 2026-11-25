import { createApp, nextTick, type App } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ClaudeCliAuthStatus from '../../src/components/ClaudeCliAuthStatus.vue'
import type { ClaudeCliAuthStatus as AuthResult } from '../../src/types'

const { request } = vi.hoisted(() => ({ request: vi.fn() }))
vi.mock('../../src/host', () => ({ host: { request } }))
let app: App | undefined
const result = (overrides: Partial<AuthResult> = {}): AuthResult => ({
  status: 'loggedOut', authMethod: 'none', source: 'desktopEmbedded', checkedAt: '2026-11-25T10:30:00Z', ...overrides,
})

beforeEach(() => { request.mockReset() })
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
    expect(request).toHaveBeenCalledTimes(2)
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

  it('does not poll or request settings mutations', async () => {
    vi.useFakeTimers()
    request.mockResolvedValueOnce(result())
    mount()
    await nextTick()
    await vi.advanceTimersByTimeAsync(60_000)
    expect(request.mock.calls).toEqual([['claude.getAuthStatus']])
  })
})

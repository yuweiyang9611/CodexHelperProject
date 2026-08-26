import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AppSettings, IpcEnvelope } from '../../src/types'

type ElectronEventListener = (method: string, payload: unknown) => void
type WebViewMessageListener = (event: MessageEvent) => void

function defineWindowProperty(name: 'codexU' | 'chrome', value: unknown): void {
  Object.defineProperty(window, name, {
    configurable: true,
    writable: true,
    value,
  })
}

function clearNativeBridges(): void {
  Reflect.deleteProperty(window, 'codexU')
  Reflect.deleteProperty(window, 'chrome')
}

beforeEach(() => {
  vi.resetModules()
  vi.useRealTimers()
  clearNativeBridges()
})

afterEach(() => {
  vi.useRealTimers()
  clearNativeBridges()
})

describe('HostBridge transport selection', () => {
  it('prefers Electron requests and multiplexes cancellable Electron events by method', async () => {
    let emitElectronEvent: ElectronEventListener | undefined
    const unsubscribeElectronEvents = vi.fn()
    const electronRequest = vi.fn<(method: string, payload?: object) => Promise<unknown>>()
      .mockResolvedValue({ appVersion: '1.2.3', platform: 'win32' })
    const onEvent = vi.fn((listener: ElectronEventListener) => {
      emitElectronEvent = listener
      return unsubscribeElectronEvents
    })
    defineWindowProperty('codexU', { request: electronRequest, onEvent })

    const webViewPostMessage = vi.fn()
    const webViewAddEventListener = vi.fn()
    defineWindowProperty('chrome', {
      webview: {
        postMessage: webViewPostMessage,
        addEventListener: webViewAddEventListener,
      },
    })

    const { host } = await import('../../src/host')
    const result = await host.request<{ appVersion: string; platform: string }>('app.initialize', { ready: true })

    expect(host.isNative).toBe(true)
    expect(result).toEqual({ appVersion: '1.2.3', platform: 'win32' })
    expect(electronRequest).toHaveBeenCalledWith('app.initialize', { ready: true })
    expect(webViewPostMessage).not.toHaveBeenCalled()
    expect(webViewAddEventListener).not.toHaveBeenCalled()

    const usageHandler = vi.fn()
    const settingsHandler = vi.fn()
    const stopUsage = host.on('usage.snapshotChanged', usageHandler)
    const stopSettings = host.on('settings.changed', settingsHandler)

    expect(onEvent).toHaveBeenCalledTimes(1)
    emitElectronEvent?.('usage.snapshotChanged', { runtime: 'codex' })
    expect(usageHandler).toHaveBeenCalledWith({ runtime: 'codex' })
    expect(settingsHandler).not.toHaveBeenCalled()

    stopUsage()
    expect(unsubscribeElectronEvents).not.toHaveBeenCalled()
    emitElectronEvent?.('usage.snapshotChanged', { runtime: 'claudeCode' })
    expect(usageHandler).toHaveBeenCalledTimes(1)

    stopSettings()
    stopSettings()
    expect(unsubscribeElectronEvents).toHaveBeenCalledTimes(1)
  })

  it('falls back to the WebView2 envelope protocol when Electron is unavailable', async () => {
    let receiveWebViewMessage: WebViewMessageListener | undefined
    const postMessage = vi.fn()
    const addEventListener = vi.fn((type: 'message', listener: WebViewMessageListener) => {
      expect(type).toBe('message')
      receiveWebViewMessage = listener
    })
    defineWindowProperty('chrome', { webview: { postMessage, addEventListener } })

    const { host } = await import('../../src/host')
    const response = host.request<{ theme: string }>('settings.get')
    const request = postMessage.mock.calls[0]?.[0] as IpcEnvelope

    expect(host.isNative).toBe(true)
    expect(request).toMatchObject({ version: 1, type: 'request', method: 'settings.get', payload: {} })
    expect(request.id).toEqual(expect.any(String))

    receiveWebViewMessage?.(new MessageEvent('message', {
      data: {
        version: 1,
        id: request.id,
        type: 'response',
        ok: true,
        payload: { theme: 'dark' },
      } satisfies IpcEnvelope,
    }))

    await expect(response).resolves.toEqual({ theme: 'dark' })
  })

  it('keeps the browser mock when neither native bridge exists', async () => {
    vi.useFakeTimers()
    const { host } = await import('../../src/host')

    const settingsRequest = host.request<AppSettings>('settings.get')
    await vi.advanceTimersByTimeAsync(180)

    expect(host.isNative).toBe(false)
    await expect(settingsRequest).resolves.toMatchObject({ theme: 'dark', globalHotKey: 'Ctrl+U' })
  })
})

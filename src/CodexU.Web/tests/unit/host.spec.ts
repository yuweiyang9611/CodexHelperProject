import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AppSettings, IpcEnvelope, RateCatalogSnapshot } from '../../src/types'
import { builtInModelNames, newestBuiltInRateFor } from '../../src/builtInRates'

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

  it('offers September 22 models with dated official rates in the rate editor', async () => {
    vi.useFakeTimers()
    const { host } = await import('../../src/host')
    const request = host.request<RateCatalogSnapshot>('rates.getCatalog')
    await vi.advanceTimersByTimeAsync(180)
    const catalog = await request
    expect(catalog.builtIn).toMatchObject({ catalogVersion: '2026.09.2', publishedOn: '2026-09-22', rateCount: 32 })
    for (const [model, input, cached, output] of [
      ['gpt-6-sol', 50, 5, 250], ['gpt-6-luna', 2.5, 0.25, 12.5],
    ] as const) {
      expect(builtInModelNames(catalog.builtInRates)).toContain(model)
      expect(newestBuiltInRateFor(catalog.builtInRates, model, '2026-09-21')).toBeNull()
      expect(newestBuiltInRateFor(catalog.builtInRates, model, '2026-09-22')).toMatchObject({
        inputCreditsPerMillion: input, cachedInputCreditsPerMillion: cached, outputCreditsPerMillion: output,
        effectiveFrom: '2026-09-22', catalogVersion: '2026.09.2', matchMode: 'exact',
      })
    }
    expect(newestBuiltInRateFor(catalog.builtInRates, 'gpt-6-astra', '2026-09-22')?.catalogVersion).toBe('2026.09.1')
  })
})

import { contextBridge, ipcRenderer } from 'electron';

// Keep this preload self-contained. A sandboxed Electron preload only receives
// a restricted require implementation and cannot load our compiled local files.
const REQUEST_CHANNEL = 'codexu:request';
const EVENT_CHANNEL = 'codexu:event';

export const ALLOWED_METHODS: ReadonlySet<string> = new Set([
  'app.initialize',
  'app.ready',
  'usage.getSnapshot',
  'usage.refresh',
  'usage.getCombined',
  'runtime.select',
  'settings.get',
  'settings.update',
  'statusStrip.getState',
  'statusStrip.preview',
  'statusStrip.recover',
  'rates.getCatalog',
  'rates.export',
  'rates.import',
  'rates.reset',
  'todos.list',
  'todos.add',
  'todos.update',
  'todos.toggle',
  'todos.delete',
  'todos.clearCompleted',
  'update.check',
  'update.openRelease',
  'data.exportAggregates',
  'data.backup',
  'data.restore',
  'diagnostics.export',
  'diagnostics.rebuildIndex',
  'window.toggleCompact',
  'window.setAlwaysOnTop',
  'window.show',
  'window.hide',
]);

export const ALLOWED_EVENT_METHODS: ReadonlySet<string> = new Set([
  'app.projectionWarning',
  'settings.changed',
  'statusStrip.stateChanged',
  'usage.refreshFailed',
  'usage.refreshStarted',
  'usage.snapshotChanged',
  'window.compactChanged',
]);

type JsonObject = Record<string, unknown>;

export type CodexUEventListener = (method: string, payload: unknown) => void;

export interface CodexUBridge {
  request(method: string, payload?: JsonObject): Promise<unknown>;
  onEvent(listener: CodexUEventListener): () => void;
}

const listeners = new Set<CodexUEventListener>();

function isAllowedMethod(value: unknown): value is string {
  return typeof value === 'string'
    && value.length > 0
    && value.length <= 128
    && ALLOWED_METHODS.has(value);
}

function isAllowedEventMethod(value: unknown): value is string {
  return typeof value === 'string'
    && value.length > 0
    && value.length <= 128
    && ALLOWED_EVENT_METHODS.has(value);
}

ipcRenderer.on(EVENT_CHANNEL, (_event, method: unknown, payload: unknown) => {
  if (!isAllowedEventMethod(method)) return;

  for (const listener of listeners) {
    try {
      listener(method, payload);
    } catch (reason) {
      console.error('codexU renderer event listener failed.', reason);
    }
  }
});

const bridge: CodexUBridge = Object.freeze({
  request(method: string, payload: JsonObject = {}): Promise<unknown> {
    if (!isAllowedMethod(method)) {
      return Promise.reject(new Error(`Host method is not allowed: ${String(method)}`));
    }
    return ipcRenderer.invoke(REQUEST_CHANNEL, method, payload);
  },

  onEvent(listener: CodexUEventListener): () => void {
    if (typeof listener !== 'function') {
      throw new TypeError('Event listener must be a function.');
    }
    listeners.add(listener);
    return () => listeners.delete(listener);
  },
});

contextBridge.exposeInMainWorld('codexU', bridge);

import { MAX_FRAME_BYTES, isRecord, type JsonObject } from './protocol';

export const TRUSTED_RENDERER_ORIGIN = 'app://codexu';
const REQUEST_ID_SIZE_SENTINEL = '00000000-0000-0000-0000-000000000000';

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

export function isAllowedMethod(value: unknown): value is string {
  return typeof value === 'string'
    && value.length > 0
    && value.length <= 128
    && ALLOWED_METHODS.has(value);
}

export function isAllowedEventMethod(value: unknown): value is string {
  return typeof value === 'string'
    && value.length > 0
    && value.length <= 128
    && ALLOWED_EVENT_METHODS.has(value);
}

export function isTrustedRendererUrl(value: string | undefined | null): boolean {
  if (!value) return false;

  try {
    const url = new URL(value);
    return url.protocol === 'app:'
      && url.hostname === 'codexu'
      && url.port === ''
      && url.username === ''
      && url.password === '';
  } catch {
    return false;
  }
}

export function validateRendererPayload(
  method: string,
  value: unknown,
): asserts value is JsonObject {
  if (!isRecord(value)) {
    throw new TypeError('IPC payload must be an object.');
  }

  let serializedRequest: string | undefined;
  try {
    serializedRequest = JSON.stringify({
      version: 1,
      id: REQUEST_ID_SIZE_SENTINEL,
      type: 'request',
      method,
      payload: value,
    });
  } catch {
    throw new TypeError('IPC payload must be JSON serializable.');
  }

  if (serializedRequest === undefined) {
    throw new TypeError('IPC payload must be JSON serializable.');
  }

  if (Buffer.byteLength(serializedRequest, 'utf8') > MAX_FRAME_BYTES) {
    throw new RangeError(`IPC request frame exceeds ${MAX_FRAME_BYTES} UTF-8 bytes.`);
  }
}

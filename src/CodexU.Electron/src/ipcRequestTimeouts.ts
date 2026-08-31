const DEFAULT_REQUEST_TIMEOUT_MS = 30_000;
const SETTINGS_UPDATE_REQUEST_TIMEOUT_MS = 60_000;

/**
 * A startup-setting mutation can consume one 25-second native-host deadline,
 * then another 25-second deadline while compensating an uncertain failure.
 * Keep ten seconds beyond that nested budget for Sidecar dispatch and storage.
 */
export function requestTimeoutForMethod(method: string): number {
  if (method === 'settings.update') return SETTINGS_UPDATE_REQUEST_TIMEOUT_MS;
  if (method === 'usage.getCombined') return 300_000;
  if (method.startsWith('usage.') || method === 'runtime.select') return 120_000;
  return DEFAULT_REQUEST_TIMEOUT_MS;
}

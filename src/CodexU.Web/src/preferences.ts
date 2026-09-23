import type { AgentRuntime } from './types'

export function readPreference(key: string): string | null {
  try { return localStorage.getItem(`codexu.${key}`) } catch { return null }
}

export function writePreference(key: string, value: string): void {
  try { localStorage.setItem(`codexu.${key}`, value) } catch { /* Storage can be disabled by the host. */ }
}

export function preferredRuntime(): AgentRuntime | null {
  const value = readPreference('runtime')
  return value === 'codex' || value === 'claudeCode' ? value : null
}

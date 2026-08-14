#!/usr/bin/env node
// Bridges Claude Code's status line into the quota snapshot codexU reads.
//
// Claude Code keeps no local record of remaining quota, so the dashboard cannot
// fill its rings from disk on its own. The one documented machine-readable
// source is the JSON piped to a configured `statusLine` command, which carries
// rate_limits.five_hour / rate_limits.seven_day. This script is that command:
// it prints a status line as usual and, as a side effect, writes the snapshot.
//
// Install by adding to %USERPROFILE%\.claude\settings.json:
//
//   { "statusLine": { "type": "command",
//                     "command": "node ~/.claude/claude-statusline-snapshot.mjs",
//                     "refreshInterval": 30 } }
//
// Use forward slashes even on Windows: Claude Code runs the command through Git
// Bash when it is installed, and Git Bash eats unquoted backslashes silently.

import { mkdirSync, renameSync, writeFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { dirname, join } from 'node:path'

const FIVE_HOUR_MINUTES = 300
const SEVEN_DAY_MINUTES = 7 * 24 * 60

/** Where the desktop app looks first. Mirrors ClaudeCodeUsageReader. */
export function snapshotPath(env = process.env) {
  const base = env.LOCALAPPDATA ?? join(homedir(), 'AppData', 'Local')
  return join(base, 'codexU', 'claude-code', 'statusline-snapshot.json')
}

/**
 * Maps one rate-limit window onto the shape the reader expects. Returns null
 * when the window is absent or unusable, so a partial payload contributes only
 * the windows it actually has rather than fabricating zeroes.
 */
export function toWindow(source, windowDurationMinutes) {
  if (!source || typeof source !== 'object') return null

  const used = Number(source.used_percentage)
  if (!Number.isFinite(used)) return null

  const window = {
    usedPercent: Math.min(100, Math.max(0, used)),
    windowDurationMinutes,
  }

  // resets_at is Unix epoch seconds; the reader parses an ISO 8601 string.
  const resetsAt = Number(source.resets_at)
  if (Number.isFinite(resetsAt) && resetsAt > 0) {
    window.resetsAt = new Date(resetsAt * 1000).toISOString()
  }
  return window
}

/**
 * Builds the snapshot, or null when the payload carries no usable window.
 * Returning null matters: rate_limits is absent on API-key, Bedrock, Vertex and
 * Foundry auth, and absent on subscription auth until the first API response of
 * a session. Writing an empty snapshot in those cases would replace good data
 * with an authoritative-looking blank, and the reader would report it as read.
 */
export function buildSnapshot(payload) {
  const limits = payload?.rate_limits
  if (!limits || typeof limits !== 'object') return null

  const primary = toWindow(limits.five_hour, FIVE_HOUR_MINUTES)
  const secondary = toWindow(limits.seven_day, SEVEN_DAY_MINUTES)
  if (!primary && !secondary) return null

  const snapshot = { source: 'claude-code-statusline', capturedAt: new Date().toISOString() }
  if (primary) snapshot.primary = primary
  if (secondary) snapshot.secondary = secondary
  return snapshot
}

/** Atomic so the desktop app never reads a half-written file. */
export function writeSnapshot(snapshot, path = snapshotPath()) {
  mkdirSync(dirname(path), { recursive: true })
  const temporary = `${path}.tmp`
  writeFileSync(temporary, `${JSON.stringify(snapshot, null, 2)}\n`, 'utf8')
  renameSync(temporary, path)
}

function bar(percent) {
  const filled = Math.round(Math.min(100, Math.max(0, percent)) / 10)
  return `${'#'.repeat(filled)}${'-'.repeat(10 - filled)}`
}

export function statusLine(payload, snapshot) {
  const parts = []
  const model = payload?.model?.display_name ?? payload?.model?.id
  if (model) parts.push(model)

  const context = Number(payload?.context_window?.used_percentage)
  if (Number.isFinite(context)) parts.push(`ctx ${context.toFixed(0)}%`)

  if (snapshot?.primary) parts.push(`5h ${bar(snapshot.primary.usedPercent)} ${snapshot.primary.usedPercent.toFixed(0)}%`)
  if (snapshot?.secondary) parts.push(`7d ${bar(snapshot.secondary.usedPercent)} ${snapshot.secondary.usedPercent.toFixed(0)}%`)

  return parts.join('  |  ')
}

function readStdin() {
  return new Promise(resolve => {
    let raw = ''
    process.stdin.setEncoding('utf8')
    process.stdin.on('data', chunk => { raw += chunk })
    process.stdin.on('end', () => resolve(raw))
    process.stdin.on('error', () => resolve(raw))
  })
}

async function main() {
  const raw = await readStdin()
  let payload
  try {
    payload = JSON.parse(raw)
  } catch {
    // A status line command must never be the reason a session looks broken.
    process.stdout.write('')
    return
  }

  let snapshot = null
  try {
    snapshot = buildSnapshot(payload)
    if (snapshot) writeSnapshot(snapshot)
  } catch {
    // Losing the snapshot is recoverable on the next refresh; failing the status
    // line is visible to the user on every keystroke. Prefer the former.
    snapshot = snapshot ?? null
  }

  process.stdout.write(statusLine(payload, snapshot))
}

// Only run the pipeline when invoked as the status line command, so the pure
// helpers above stay importable by tests.
if (process.argv[1] && import.meta.url.endsWith(process.argv[1].replace(/\\/g, '/').split('/').pop())) {
  await main()
}

import type { RateLimitWindow } from './types'

export function isDesktopQuota(quota?: RateLimitWindow | null): boolean {
  return isDesktopHistoryQuota(quota) || isDesktopQueryQuota(quota)
}

export function isDesktopHistoryQuota(quota?: RateLimitWindow | null): boolean {
  return quota?.source === 'claude-desktop-history'
}

export function isDesktopQueryQuota(quota?: RateLimitWindow | null): boolean {
  return quota?.source === 'claude-desktop-get-usage'
}

export function quotaQueryTime(value?: string | null): string {
  if (!value || !Number.isFinite(Date.parse(value))) return '读取时间不可用'
  return `读取于 ${quotaSampleTime(value)}`
}

export function quotaSampleTime(value?: string | null): string {
  if (!value || !Number.isFinite(Date.parse(value))) return '采样时间不可用'
  return new Intl.DateTimeFormat('zh-CN', {
    year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23',
  }).format(new Date(value))
}

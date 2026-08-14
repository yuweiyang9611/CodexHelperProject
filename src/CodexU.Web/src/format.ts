import type { DataQuality } from './types'

export function qualityLabel(quality?: DataQuality): string {
  if (quality === 'detailed') return '精细统计'
  if (quality === 'partial') return '部分数据'
  if (quality === 'approximate') return '近似统计'
  return '暂无数据'
}

export function compactNumber(value?: number | null): string {
  if (value == null) return '--'
  if (value >= 1_000_000_000) return `${trim(value / 1_000_000_000)}B`
  if (value >= 1_000_000) return `${trim(value / 1_000_000)}M`
  if (value >= 1_000) return `${trim(value / 1_000)}K`
  return Math.round(value).toLocaleString()
}

export function creditAmount(
  value?: number | null,
  amountPerThousandCredits = 40,
  currencySymbol = 'US$',
): string {
  if (value == null) return '--'
  const safeValue = Number.isFinite(value) ? Math.max(0, value) : 0
  const safeRate = Number.isFinite(amountPerThousandCredits) && amountPerThousandCredits > 0
    ? amountPerThousandCredits
    : 40
  const amount = safeValue / 1_000 * safeRate
  const formatted = new Intl.NumberFormat('zh-CN', {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(amount)
  return `${currencySymbol || 'US$'}${formatted}`
}

export function currencyAmount(
  value?: number | null,
  currencySymbol = 'US$',
  compact = false,
  showPositiveSign = false,
): string {
  if (value == null || !Number.isFinite(value)) return '--'
  const sign = value < 0 ? '-' : showPositiveSign && value > 0 ? '+' : ''
  const absolute = Math.abs(value)
  let formatted: string
  if (compact && absolute >= 1_000_000) {
    formatted = `${trimCurrency(absolute / 1_000_000)}M`
  } else if (compact && absolute >= 1_000) {
    formatted = `${trimCurrency(absolute / 1_000)}K`
  } else {
    formatted = new Intl.NumberFormat('zh-CN', {
      minimumFractionDigits: 2,
      maximumFractionDigits: 2,
    }).format(absolute)
  }

  return `${sign}${currencySymbol || 'US$'}${formatted}`
}

export function relativeTime(value?: string | null): string {
  if (!value) return '暂无时间'
  const delta = Date.now() - new Date(value).getTime()
  const minutes = Math.max(0, Math.floor(delta / 60_000))
  if (minutes < 1) return '刚刚'
  if (minutes < 60) return `${minutes} 分钟前`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours} 小时前`
  return `${Math.floor(hours / 24)} 天前`
}

export function resetTime(value?: string | null): string {
  if (!value) return '重置时间不可用'
  const delta = new Date(value).getTime() - Date.now()
  if (delta <= 0) return '即将重置'
  const minutes = Math.ceil(delta / 60_000)
  if (minutes < 60) return `${minutes} 分钟后重置`
  const hours = Math.floor(minutes / 60)
  if (hours < 48) return `${hours} 小时后重置`
  return `${Math.floor(hours / 24)} 天后重置`
}

/**
 * Reads the projection off `exhaustsAt` rather than the `timeToExhaustion` span, so
 * the countdown keeps shrinking between refreshes instead of freezing at whatever it
 * said when the snapshot was taken.
 */
export function exhaustionTime(value?: string | null): string {
  if (!value) return ''
  const delta = new Date(value).getTime() - Date.now()
  if (!Number.isFinite(delta)) return ''
  if (delta <= 0) return '预计即将耗尽'
  const minutes = Math.ceil(delta / 60_000)
  if (minutes < 60) return `预计 ${minutes} 分钟后耗尽`
  const hours = Math.floor(minutes / 60)
  if (hours < 48) return `预计 ${hours} 小时后耗尽`
  return `预计 ${Math.floor(hours / 24)} 天后耗尽`
}

function trim(value: number): string {
  return value >= 100 ? value.toFixed(0) : value >= 10 ? value.toFixed(1) : value.toFixed(2)
}

function trimCurrency(value: number): string {
  return value >= 100 ? value.toFixed(0) : value >= 10 ? value.toFixed(1) : value.toFixed(2)
}

import type { ModelCreditRate, RateCatalogSnapshot, RateEditorPreview } from './types'

// Browser-only fixtures for the mock host. Native builds request the full
// catalog and preview from .NET; this sample only exercises the editor controls.
export function builtInModelNames(rates: ModelCreditRate[]): string[] {
  return [...new Set(rates.map(r => r.model))].sort()
}

export function newestBuiltInRateFor(rates: ModelCreditRate[], model: string, date: string) {
  return rates.filter(r => r.model === model && (!r.effectiveFrom || r.effectiveFrom <= date))
    .sort((a, b) => (b.effectiveFrom ?? '').localeCompare(a.effectiveFrom ?? ''))[0] ?? null
}

export function demoRatePreview(payload: { rates?: ModelCreditRate[]; model?: string; effectiveOn?: string }, pinned: boolean): RateEditorPreview {
  const { rates = [], model, effectiveOn } = payload
  const date = new Date()
  const today = [date.getFullYear(), String(date.getMonth() + 1).padStart(2, '0'), String(date.getDate()).padStart(2, '0')].join('-')
  const catalog = demoRateCatalog.builtInRates
  const keys = new Set<string>()
  let validationMessage: string | null = null
  for (const rate of rates) {
    const key = [rate.model, rate.effectiveFrom || ''].join('/')
    if (!rate.model.trim()) validationMessage = '模型名称不能为空。'
    else if ([rate.inputCreditsPerMillion, rate.cachedInputCreditsPerMillion, rate.outputCreditsPerMillion].some(n => !Number.isFinite(n) || n < 0 || n > 1_000_000))
      validationMessage = '请填写有效数值。'
    else if (keys.has(key)) validationMessage = '同一生效日期存在重复费率。'
    keys.add(key)
    if (validationMessage) break
  }
  const canAdd = !pinned && !validationMessage && rates.length < 200
  const name = builtInModelNames(catalog).find(s => !rates.some(r => r.model === s) && newestBuiltInRateFor(catalog, s, today)) ?? ''
  const base = newestBuiltInRateFor(catalog, name, today)
  const selected = model ? newestBuiltInRateFor(catalog, model, effectiveOn || today) : null
  return {
    validationMessage, customOverrideCount: rates.length, versionCount: new Set(rates.map(r => r.catalogVersion)).size, canAdd,
    nextRate: canAdd ? { model: name, inputCreditsPerMillion: base?.inputCreditsPerMillion ?? 0,
      cachedInputCreditsPerMillion: base?.cachedInputCreditsPerMillion ?? 0, outputCreditsPerMillion: base?.outputCreditsPerMillion ?? 0,
      effectiveFrom: today, source: '用户自定义', catalogVersion: 'custom-' + today.replaceAll('-', '.'), matchMode: 'exact' } : null,
    builtInRate: selected, effectiveRate: model ? newestBuiltInRateFor(rates, model, effectiveOn || today) ?? selected : null,
  }
}

export const demoRateCatalog: RateCatalogSnapshot = {
  builtIn: {
    schemaVersion: 1,
    catalogVersion: '2026.11.1',
    source: '内置费率目录（OpenAI API Standard 短上下文价目 + 历史 Credits 参考表 + Anthropic 公布价目）',
    publishedOn: '2026-11-01',
    rateCount: 41,
  },
  // A representative slice of the real catalog rather than an empty list:
  // the rate editor seeds a new row from these, so an empty array would
  // leave the browser demo — and the visual baseline — exercising the
  // zero-filled path the defaults exist to replace. Credits are USD/M x 25.
  builtInRates: [
    { model: 'claude-opus-5', inputCreditsPerMillion: 125, cachedInputCreditsPerMillion: 12.5, outputCreditsPerMillion: 625, effectiveFrom: null, source: 'Anthropic 公布的 Claude API 价目', catalogVersion: 'anthropic-2026.07.1', matchMode: 'exact' },
    { model: 'claude-sonnet-5', inputCreditsPerMillion: 50, cachedInputCreditsPerMillion: 5, outputCreditsPerMillion: 250, effectiveFrom: null, source: 'Anthropic 公布的 Claude API 价目', catalogVersion: 'anthropic-2026.07.1', matchMode: 'exact' },
    { model: 'claude-haiku-4-5', inputCreditsPerMillion: 25, cachedInputCreditsPerMillion: 2.5, outputCreditsPerMillion: 125, effectiveFrom: null, source: 'Anthropic 公布的 Claude API 价目', catalogVersion: 'anthropic-2026.07.1', matchMode: 'exact' },
    { model: 'gpt-5.2', inputCreditsPerMillion: 43.75, cachedInputCreditsPerMillion: 4.375, outputCreditsPerMillion: 350, effectiveFrom: null, source: '用户提供的 OpenAI Credits 参考表', catalogVersion: '2026.07.1', matchMode: 'exact' },
    { model: 'gpt-5.4-mini', inputCreditsPerMillion: 18.75, cachedInputCreditsPerMillion: 1.875, outputCreditsPerMillion: 112.5, effectiveFrom: '2026-03-17', source: 'OpenAI API 官方 Standard 短上下文价目与 Changelog', catalogVersion: '2026.03.1', matchMode: 'exact' },
    { model: 'gpt-5.6-sol', inputCreditsPerMillion: 125, cachedInputCreditsPerMillion: 12.5, outputCreditsPerMillion: 750, effectiveFrom: null, source: '用户提供的 OpenAI Credits 参考表', catalogVersion: '2026.07.1', matchMode: 'exact' },
    { model: 'gpt-5.6-terra', inputCreditsPerMillion: 50, cachedInputCreditsPerMillion: 5, outputCreditsPerMillion: 300, effectiveFrom: '2026-07-30', source: 'OpenAI API 官方 Standard 短上下文价目与 Changelog', catalogVersion: '2026.07.2', matchMode: 'exact' },
    { model: 'gpt-5.6-luna', inputCreditsPerMillion: 5, cachedInputCreditsPerMillion: 0.5, outputCreditsPerMillion: 30, effectiveFrom: '2026-07-30', source: 'OpenAI API 官方 Standard 短上下文价目与 Changelog', catalogVersion: '2026.07.2', matchMode: 'exact' },
    { model: 'gpt-5.6-cyber', inputCreditsPerMillion: 312.5, cachedInputCreditsPerMillion: 31.25, outputCreditsPerMillion: 1875, effectiveFrom: '2026-08-07', source: 'OpenAI API 官方 Standard 短上下文价目与 Changelog', catalogVersion: '2026.08.1', matchMode: 'exact' },
    { model: 'gpt-daybreak-blue', inputCreditsPerMillion: 125, cachedInputCreditsPerMillion: 12.5, outputCreditsPerMillion: 750, effectiveFrom: '2026-08-07', source: 'OpenAI API 官方 Standard 短上下文价目与 Changelog', catalogVersion: '2026.08.1', matchMode: 'exact' },
    { model: 'gpt-daybreak-red', inputCreditsPerMillion: 312.5, cachedInputCreditsPerMillion: 31.25, outputCreditsPerMillion: 1875, effectiveFrom: '2026-08-07', source: 'OpenAI API 官方 Standard 短上下文价目与 Changelog', catalogVersion: '2026.08.1', matchMode: 'exact' },
    { model: 'gpt-5.6-sol', inputCreditsPerMillion: 100, cachedInputCreditsPerMillion: 10, outputCreditsPerMillion: 500, effectiveFrom: '2026-08-21', source: 'OpenAI API 官方 Standard 短上下文价目（GPT-5.6 Sol 促销价，至少持续至 2026-11-21）', catalogVersion: '2026.08.2', matchMode: 'exact' },
    { model: 'gpt-daybreak-blue', inputCreditsPerMillion: 100, cachedInputCreditsPerMillion: 10, outputCreditsPerMillion: 500, effectiveFrom: '2026-08-21', source: 'OpenAI API 官方 Standard 短上下文价目（GPT-5.6 Sol 促销价，至少持续至 2026-11-21）', catalogVersion: '2026.08.2', matchMode: 'exact' },
    { model: 'gpt-6-astra', inputCreditsPerMillion: 250, cachedInputCreditsPerMillion: 25, outputCreditsPerMillion: 1250, effectiveFrom: '2026-09-03', source: 'OpenAI API 官方 Standard 短上下文价目与 Changelog', catalogVersion: '2026.09.1', matchMode: 'exact' },
    { model: 'gpt-6-sol', inputCreditsPerMillion: 50, cachedInputCreditsPerMillion: 5, outputCreditsPerMillion: 250, effectiveFrom: '2026-09-22', source: 'OpenAI API 官方 Standard 短上下文价目与 Changelog', catalogVersion: '2026.09.2', matchMode: 'exact' },
    { model: 'gpt-6-luna', inputCreditsPerMillion: 2.5, cachedInputCreditsPerMillion: 0.25, outputCreditsPerMillion: 12.5, effectiveFrom: '2026-09-22', source: 'OpenAI API 官方 Standard 短上下文价目与 Changelog', catalogVersion: '2026.09.2', matchMode: 'exact' },
    { model: 'gpt-6.1-sol', inputCreditsPerMillion: 50, cachedInputCreditsPerMillion: 2.5, outputCreditsPerMillion: 250, effectiveFrom: '2026-09-29', source: 'OpenAI API 官方 Standard 短上下文价目与 Changelog', catalogVersion: '2026.10.1', matchMode: 'exact' },
    { model: 'gpt-rosalind-research', inputCreditsPerMillion: 125, cachedInputCreditsPerMillion: 12.5, outputCreditsPerMillion: 625, effectiveFrom: '2026-10-05', source: 'OpenAI API 官方专业模型价目与 Changelog（GPT-Rosalind 自 2026-10-05 起计费）', catalogVersion: '2026.10.1', matchMode: 'exact' },
    { model: 'claude-fable-5-1', inputCreditsPerMillion: 250, cachedInputCreditsPerMillion: 6.25, outputCreditsPerMillion: 1250, effectiveFrom: '2026-09-01', source: 'Anthropic 官方 Claude API 模型页与发布记录', catalogVersion: 'anthropic-2026.10.1', matchMode: 'exact' },
    { model: 'claude-mythos-5-1', inputCreditsPerMillion: 250, cachedInputCreditsPerMillion: 6.25, outputCreditsPerMillion: 1250, effectiveFrom: '2026-09-01', source: 'Anthropic 官方 Claude API 模型页与发布记录', catalogVersion: 'anthropic-2026.10.1', matchMode: 'exact' },
    { model: 'claude-opus-5-5', inputCreditsPerMillion: 100, cachedInputCreditsPerMillion: 5, outputCreditsPerMillion: 500, effectiveFrom: '2026-09-22', source: 'Anthropic 官方 Claude API 模型页与发布记录', catalogVersion: 'anthropic-2026.10.1', matchMode: 'exact' },
    { model: 'claude-sonnet-5-5', inputCreditsPerMillion: 50, cachedInputCreditsPerMillion: 5, outputCreditsPerMillion: 250, effectiveFrom: '2026-09-28', source: 'Anthropic 官方 Claude API 模型页与发布记录', catalogVersion: 'anthropic-2026.10.1', matchMode: 'exact' },
    { model: 'gpt-6.1-terra', inputCreditsPerMillion: 25, cachedInputCreditsPerMillion: 1.25, outputCreditsPerMillion: 125, effectiveFrom: '2026-10-26', source: 'OpenAI API 官方 Standard 短上下文价目与 Changelog', catalogVersion: '2026.11.1', matchMode: 'exact' },
    { model: 'gpt-6-rosalind', inputCreditsPerMillion: 375, cachedInputCreditsPerMillion: 37.5, outputCreditsPerMillion: 1875, effectiveFrom: '2026-10-30', source: 'OpenAI API 官方专业模型价目与 Changelog（GPT-6 Rosalind 自 2026-10-30 发布）', catalogVersion: '2026.11.1', matchMode: 'exact' },
    { model: 'claude-sonnet-5-5', inputCreditsPerMillion: 50, cachedInputCreditsPerMillion: 2.5, outputCreditsPerMillion: 250, effectiveFrom: '2026-10-07', source: 'Anthropic 官方 Claude API 模型页与发布记录（短上下文基础价格）', catalogVersion: 'anthropic-2026.11.1', matchMode: 'exact' },
    { model: 'claude-haiku-5-5', inputCreditsPerMillion: 2.5, cachedInputCreditsPerMillion: 0.25, outputCreditsPerMillion: 12.5, effectiveFrom: '2026-10-07', source: 'Anthropic 官方 Claude API 模型页与发布记录（短上下文基础价格）', catalogVersion: 'anthropic-2026.11.1', matchMode: 'exact' },
  ],
}

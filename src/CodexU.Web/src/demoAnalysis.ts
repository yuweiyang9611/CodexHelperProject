import type { TokenBreakdown, UsageAnalysisEntry, UsageAnalysisGroup, UsageAnalysisRequest, UsageAnalysisResult, UsageAnalysisTotals, UsageSessionGroup } from './types'
const localDate = (date: Date) => `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
const availableBreakdownFields = ['inputTokens', 'cachedInputTokens', 'outputTokens']

function breakdown(tokens = 0): TokenBreakdown {
  const input = Math.floor(tokens * .8), cached = Math.floor(tokens * .3)
  return { inputTokens: input, cachedInputTokens: cached, outputTokens: tokens - input, reasoningOutputTokens: 0, totalTokens: tokens,
    uncachedInputTokens: input - cached, billableCachedInputTokens: cached, visibleTotalTokens: tokens,
    cacheWrite5mTokens: 0, cacheWrite1hTokens: 0, billableCacheWrite5mTokens: 0, billableCacheWrite1hTokens: 0, billableCacheWriteTokens: 0 }
}

export function createDemoAnalysis(request: UsageAnalysisRequest, now: Date): UsageAnalysisResult {
  const model = request.runtime === 'codex' ? 'gpt-6-astra' : 'claude-sonnet-5'
  const other = request.runtime === 'codex' ? 'gpt-5.6-sol' : 'claude-opus-5'
  const entries: UsageAnalysisEntry[] = []
  for (let index = 0; index < 220; index++) {
    const date = new Date(now); date.setDate(now.getDate() - index)
    const day = localDate(date)
    const tokens = (10000 + (index * 7349) % 70000) * (request.runtime === 'codex' ? 4 : 1)
    entries.push({ date: day, sessionId: `demo-${index}`, parentSessionId: null, title: index === 0 ? '应用用量分析' : index % 4 ? `项目会话 ${index + 1}` : null,
      project: index % 3 ? 'D:\\Projects\\codexU' : 'D:\\Projects\\research', model: index % 3 ? model : other, feature: 'tasks', tokens,
      breakdown: breakdown(tokens), availableBreakdownFields, creditsUsed: tokens / 1000, rate: { model, inputCreditsPerMillion: 100, cachedInputCreditsPerMillion: 10, outputCreditsPerMillion: 500, source: '演示费率', catalogVersion: 'demo', effectiveFrom: null }, source: index < 10 ? 'live' : 'retained' })
    if (index === 0) entries.push({ ...entries.at(-1)!, sessionId: 'demo-child', parentSessionId: 'demo-0', title: '样式检查', feature: 'subagents', model, tokens: 18000, breakdown: breakdown(18000), creditsUsed: 18 })
    if (index === 2) entries.push({ date: day, sessionId: null, parentSessionId: null, title: null, project: null, model: 'unknown', feature: 'unknown', tokens: 42000, breakdown: null, creditsUsed: null, rate: null, source: 'legacy', explanation: '旧日汇总仍有 42,000 Token 缺少同一范围内可确认的原始日志，不能还原为真实会话。' })
  }
  const totals = (rows: UsageAnalysisEntry[]): UsageAnalysisTotals => {
    const sum = breakdown()
    for (const row of rows) if (row.breakdown) for (const key of Object.keys(sum) as (keyof TokenBreakdown)[]) sum[key] += row.breakdown[key]
    const rated = rows.filter(row => row.creditsUsed != null)
    return { tokens: rows.reduce((sum, row) => sum + row.tokens, 0), breakdown: sum, availableBreakdownFields,
      breakdownTokens: rows.filter(row => row.breakdown != null).reduce((sum, row) => sum + row.tokens, 0),
      creditsUsed: rated.length ? rated.reduce((sum, row) => sum + row.creditsUsed!, 0) : null,
      ratedTokens: rated.reduce((sum, row) => sum + row.tokens, 0), unratedTokens: rows.filter(row => row.creditsUsed == null).reduce((sum, row) => sum + row.tokens, 0),
      unattributedTokens: rows.filter(row => row.sessionId == null).reduce((sum, row) => sum + row.tokens, 0) }
  }
  const selected = entries.filter(row => (!request.from || row.date >= request.from) && (!request.to || row.date <= request.to)
    && (!request.model || row.model === request.model) && (!request.project || (row.project ?? '__unknown__') === request.project))
  const groups = (key: (row: UsageAnalysisEntry) => string): UsageAnalysisGroup[] => [...new Set(selected.map(key))].map(id => ({ id, label: id === '__unknown__' ? '项目未知' : id === 'unknown' ? '未分类' : id.split('\\').at(-1)!, totals: totals(selected.filter(row => key(row) === id)) }))
  const ids = [...new Set(selected.filter(row => row.sessionId).map(row => row.parentSessionId || row.sessionId!))]
  const sessions: UsageSessionGroup[] = ids.map(id => {
    const rows = selected.filter(row => (row.parentSessionId || row.sessionId) === id)
    const from = rows.map(row => row.date).sort()[0]!, to = rows.map(row => row.date).sort().at(-1)!
    return { id, title: entries.find(row => row.sessionId === id)?.title ?? null, from, to, totals: totals(rows), members: [...new Set(rows.map(row => row.sessionId!))].map(memberId => {
      const contributions = rows.filter(row => row.sessionId === memberId), first = contributions[0]!
      return { id: memberId, parentSessionId: first.parentSessionId, title: first.title, project: first.project, from, to, totals: totals(contributions), sources: [...new Set(contributions.map(row => row.source))], contributions }
    }) }
  })
  const pageSize = request.pageSize || 25, page = Math.min(Math.max(1, request.page || 1), Math.max(1, Math.ceil(sessions.length / pageSize)))
  return { runtime: request.runtime, from: request.from ?? null, to: request.to ?? null, availableFrom: entries.map(row => row.date).sort()[0]!, availableTo: localDate(now),
    totals: totals(selected), days: [...new Set(selected.map(row => row.date))].sort().map(date => ({ date, totals: totals(selected.filter(row => row.date === date)) })),
    models: groups(row => row.model), features: groups(row => row.feature), projects: groups(row => row.project ?? '__unknown__'),
    sessions: sessions.slice((page - 1) * pageSize, page * pageSize), sessionCount: sessions.length, page, pageSize,
    unattributed: selected.filter(row => row.sessionId == null),
    legacyEstimates: selected.filter(row => row.source === 'legacy').map(row => ({ date: row.date, tokens: row.tokens + 50000, creditsUsed: 123, explanation: '仅有旧版完整日估值，与已重算明细重叠，单独保留。' })),
    availableModels: [model, other, 'unknown'], availableProjects: [{ id: 'D:\\Projects\\codexU', label: 'codexU' }, { id: 'D:\\Projects\\research', label: 'research' }, { id: '__unknown__', label: '项目未知' }], diagnostics: ['浏览器演示数据，用于展示本机原始日志、历史留存与未归属差额。'] }
}

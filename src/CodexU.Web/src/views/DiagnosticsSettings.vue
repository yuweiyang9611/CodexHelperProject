<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { currencyAmount } from '../format'
import {
  builtInModelNames as builtInModelList,
  newestBuiltInRateFor,
  nextUnoverriddenModel,
} from '../builtInRates'
import { useDashboardStore } from '../stores/dashboard'
import type { DashboardSnapshot, ModelCreditRate } from '../types'

const props = defineProps<{ snapshot: DashboardSnapshot }>()
const store = useDashboardStore()
const suggestedSubscriptionAmount = computed(() =>
  props.snapshot.account?.suggestedMonthlySubscriptionAmount ?? null)
// Only the runtime on screen can be auto-priced right now, so the hint says which of
// the two fields is actually in play rather than implying both are.
const activeManualHint = computed(() => {
  const draft = store.settingsDraft
  const autoForCurrentRuntime = props.snapshot.runtime === 'claudeCode'
    ? draft?.claudeAutoDetectSubscriptionAmount
    : draft?.codexAutoDetectSubscriptionAmount
  if (!autoForCurrentRuntime) return null
  return suggestedSubscriptionAmount.value != null
    ? `当前自动推算：${amountMoney(suggestedSubscriptionAmount.value)} · ${props.snapshot.account?.planType?.toUpperCase() ?? ''}`
    : '当前套餐无法可靠推算，将使用此备用值'
})
const customRateVersions = computed(() => new Set(
  store.settingsDraft?.customModelRates
    .map((rate) => rate.catalogVersion || 'custom') ?? [],
).size)
const builtInRateIdentities = computed(() => new Set(
  store.rateCatalog?.builtInRates.map(rateIdentity) ?? [],
))
const customOverrideCount = computed(() =>
  store.settingsDraft?.customModelRates.filter((rate) =>
    !builtInRateIdentities.value.has(rateIdentity(rate))).length ?? 0)
const configuredRateCount = computed(() => store.settingsDraft?.customModelRates.length ?? 0)
const isPinnedRateCatalog = computed(() => store.settingsDraft?.isRateCatalogPinned ?? false)
const rateEntryLimit = computed(() => isPinnedRateCatalog.value ? 1000 : 200)
const settingsBusy = computed(() => store.isRunningLocalOperation || store.isUpdatingSettings)
const rateValidationMessage = computed(() => {
  const rateCount = isPinnedRateCatalog.value ? configuredRateCount.value : customOverrideCount.value
  if (rateCount > rateEntryLimit.value) {
    return isPinnedRateCatalog.value
      ? '锁定的完整费率目录不能超过 1000 条。'
      : '自定义费率不能超过 200 条；内置基线不计入该限制。'
  }
  const seen = new Set<string>()
  for (const rate of store.settingsDraft?.customModelRates ?? []) {
    const model = normalizeRateModel(rate.model)
    if (!model) return '每条自定义费率都必须填写模型名称。'
    const values: unknown[] = [
      rate.inputCreditsPerMillion,
      rate.cachedInputCreditsPerMillion,
      rate.outputCreditsPerMillion,
    ]
    if (values.some((value) => typeof value !== 'number'
      || !Number.isFinite(value)
      || value < 0
      || value > 1_000_000)) {
      return `模型 ${rate.model} 的三类费率必须是 0 到 1,000,000 之间的有效数值。`
    }
    const key = `${model}\u001f${rate.effectiveFrom || ''}`
    if (seen.has(key)) return `模型 ${rate.model} 在同一生效日期存在重复费率。`
    seen.add(key)
  }
  return null
})
const rateEditorOpen = ref(isPinnedRateCatalog.value || Boolean(rateValidationMessage.value))

// A validation error may reveal the advanced editor, but resolving that error must
// never collapse the fields out from under the user's pointer or keyboard focus.
watch([isPinnedRateCatalog, rateValidationMessage], ([pinned, validation]) => {
  if (pinned || validation) rateEditorOpen.value = true
})

function syncRateEditorState(event: Event) {
  rateEditorOpen.value = (event.currentTarget as HTMLDetailsElement).open
}

function amountMoney(value?: number | null) {
  return currencyAmount(value, 'US$')
}

function normalizeRateModel(model: string) {
  let normalized = model.trim().toLowerCase().replaceAll('_', '-').replaceAll(' ', '-')
  if (normalized.endsWith('-latest')) normalized = normalized.slice(0, -'-latest'.length)
  if (normalized === 'gpt-5.2-codex') return 'gpt-5.2'
  return normalized
}

function rateIdentity(rate: ModelCreditRate) {
  return [
    normalizeRateModel(rate.model),
    rate.effectiveFrom || '',
    rate.inputCreditsPerMillion,
    rate.cachedInputCreditsPerMillion,
    rate.outputCreditsPerMillion,
    rate.catalogVersion || '',
    rate.source || '',
    rate.matchMode || 'exact',
  ].join('\u001f')
}

const builtInModelNames = computed(() => builtInModelList(store.rateCatalog?.builtInRates))

/**
 * Copies the official rate for a model into the row being edited.
 *
 * A blank row is a bad starting point: the user has to already know that
 * claude-opus-5 is 125 / 12.5 / 625 to fill it in, and a wrong digit silently
 * misprices every future month. Starting from the current official figure makes
 * an override an edit rather than a recall exercise.
 */
function applyBuiltInDefaults(rate: ModelCreditRate) {
  const builtIn = newestBuiltInRateFor(store.rateCatalog?.builtInRates, rate.model)
  if (!builtIn) return
  rate.inputCreditsPerMillion = builtIn.inputCreditsPerMillion
  rate.cachedInputCreditsPerMillion = builtIn.cachedInputCreditsPerMillion
  rate.outputCreditsPerMillion = builtIn.outputCreditsPerMillion
}

function addCustomRate() {
  if (!store.settingsDraft || isPinnedRateCatalog.value || configuredRateCount.value >= rateEntryLimit.value) return
  const now = new Date()
  const effectiveFrom = [
    now.getFullYear(),
    String(now.getMonth() + 1).padStart(2, '0'),
    String(now.getDate()).padStart(2, '0'),
  ].join('-')
  store.settingsDraft.customModelRates ??= []
  // Seeded with the first built-in model not already overridden, at its current
  // official rate — so the row starts from a correct figure the user edits,
  // rather than from zeros that would price everything at nothing if saved.
  const model = nextUnoverriddenModel(store.rateCatalog?.builtInRates, store.settingsDraft.customModelRates)
  const builtIn = newestBuiltInRateFor(store.rateCatalog?.builtInRates, model)
  store.settingsDraft.customModelRates.push({
    model,
    inputCreditsPerMillion: builtIn?.inputCreditsPerMillion ?? 0,
    cachedInputCreditsPerMillion: builtIn?.cachedInputCreditsPerMillion ?? 0,
    outputCreditsPerMillion: builtIn?.outputCreditsPerMillion ?? 0,
    effectiveFrom,
    source: '用户自定义',
    catalogVersion: `custom-${effectiveFrom.replaceAll('-', '.')}`,
    matchMode: 'exact',
  })
}

function removeCustomRate(index: number) {
  store.settingsDraft?.customModelRates.splice(index, 1)
}
</script>

<template>
  <div class="diagnostics-layout">
    <article class="inner-card diagnostic-card" aria-labelledby="diagnostics-title">
      <div class="inner-heading">
        <div><span>本地数据</span><h3 id="diagnostics-title">数据源状态</h3></div>
        <em>{{ snapshot.indexStatus.parsedFiles }} 个文件已解析</em>
      </div>
      <div class="diagnostic-list" aria-live="polite">
        <div v-for="(diagnostic, index) in snapshot.diagnostics" :key="index">
          <!-- Severity is inferred from wording, so this pattern has to be kept in
               step with the diagnostic strings the readers emit. -->
          <i :class="{ warning: /失败|未找到|无权|暂无|尚未|不可用|限流/.test(diagnostic) }" aria-hidden="true" />
          <span>{{ diagnostic }}</span>
        </div>
      </div>
    </article>

    <article class="inner-card update-card" aria-labelledby="update-title">
      <div class="inner-heading">
        <div><span>应用更新</span><h3 id="update-title">版本与更新</h3></div>
        <em role="status" aria-live="polite" aria-atomic="true" :class="{ positive: store.updateStatus?.isUpdateAvailable }">{{ store.updateStatus?.status ?? '尚未检查' }}</em>
      </div>
      <div class="update-summary">
        <span><small>当前版本</small><strong>v{{ store.updateStatus?.currentVersion ?? store.appVersion }}</strong></span>
        <span><small>最新版本</small><strong>{{ store.updateStatus?.latestVersion ? `v${store.updateStatus.latestVersion}` : '--' }}</strong></span>
        <span><small>上次检查</small><strong>{{ store.updateStatus ? new Date(store.updateStatus.checkedAt).toLocaleString() : '--' }}</strong></span>
      </div>
      <p v-if="store.updateStatus?.notes" class="update-notes">{{ store.updateStatus.notes }}</p>
      <div class="settings-actions">
        <button class="discard-settings" :disabled="store.isCheckingUpdates" @click="store.checkForUpdates(true)">{{ store.isCheckingUpdates ? '正在检查…' : '立即检查' }}</button>
        <button class="save-settings" @click="store.openReleasePage()">打开发布页</button>
      </div>
      <p class="setting-hint">私有仓库可通过进程环境变量 CODEXU_GITHUB_TOKEN 只读检查 Release；应用不会保存或展示该令牌。</p>
    </article>

    <article class="inner-card maintenance-card" aria-labelledby="maintenance-title">
      <div class="inner-heading">
        <div><span>维护工具</span><h3 id="maintenance-title">导出、备份与修复</h3></div>
        <em role="status" aria-live="polite" aria-atomic="true">{{ store.operationStatus ?? '仅操作本机文件' }}</em>
      </div>
      <div class="maintenance-actions">
        <button :disabled="settingsBusy" @click="store.runLocalOperation('data.exportAggregates', { format: 'json' })">导出 JSON 统计</button>
        <button :disabled="settingsBusy" @click="store.runLocalOperation('data.exportAggregates', { format: 'csv' })">导出 CSV 日报</button>
        <button :disabled="settingsBusy || store.settingsDirty" title="请先保存或放弃当前设置更改" @click="store.runLocalOperation('data.backup')">备份设置与待办</button>
        <button :disabled="settingsBusy || store.settingsDirty" title="请先保存或放弃当前设置更改" @click="store.runLocalOperation('data.restore')">恢复备份</button>
        <button :disabled="settingsBusy" @click="store.runLocalOperation('diagnostics.export')">生成脱敏诊断包</button>
        <button class="warning" :disabled="settingsBusy" @click="store.runLocalOperation('diagnostics.rebuildIndex')">安全重建索引</button>
      </div>
      <p class="setting-hint">聚合报表不包含正文、任务标题、账户邮箱和完整项目路径；恢复备份前会再次确认。</p>
    </article>

    <article v-if="store.settingsDraft" class="inner-card settings-card" aria-labelledby="settings-title">
      <div class="inner-heading">
        <div><span>个性化</span><h3 id="settings-title">应用设置</h3></div>
        <em>设置会保存在本机</em>
      </div>
      <div class="settings-groups">
        <fieldset class="settings-group">
          <legend>常规</legend>
          <p>调整界面呈现、刷新频率和日常显示内容。</p>
          <div class="settings-grid">
            <label>界面主题<select v-model="store.settingsDraft.theme"><option value="dark">深色</option><option value="light">浅色玻璃</option><option value="system">跟随系统</option></select></label>
            <label>自动刷新（分钟）<input v-model.number="store.settingsDraft.autoRefreshMinutes" type="number" min="1" max="60" /></label>
            <label>界面缩放 %<input v-model.number="store.settingsDraft.uiScalePercent" type="number" min="90" max="140" step="5" /><small class="setting-hint">只使用你选择的 90%–140%；窗口变窄时界面会自动重排。</small></label>
          </div>
          <div class="setting-checks">
            <label><input v-model="store.settingsDraft.showSubagents" type="checkbox" />显示子代理任务</label>
            <label><input v-model="store.settingsDraft.checkForUpdates" type="checkbox" />每天自动检查更新</label>
            <label><input v-model="store.settingsDraft.includePrereleaseUpdates" type="checkbox" />接收预发布版本</label>
          </div>
        </fieldset>

        <fieldset class="settings-group">
          <legend>数据源</legend>
          <p>留空时自动发现本机 Codex 数据和可执行文件。</p>
          <div class="settings-grid settings-grid-wide">
            <label class="settings-field-wide">Codex 数据目录<input v-model="store.settingsDraft.codexHome" placeholder="自动：优先 CODEX_HOME" /></label>
            <label class="settings-field-wide">Codex 可执行文件<input v-model="store.settingsDraft.codexExecutable" placeholder="自动发现 ChatGPT/Codex CLI，也可手动指定 codex.exe" /></label>
            <label class="settings-field-wide">默认工作范围<input v-model="store.settingsDraft.defaultWorkspace" placeholder="例如 D:\Workspace" /></label>
          </div>
          <div class="setting-checks">
            <label><input v-model="store.settingsDraft.incrementalIndexEnabled" type="checkbox" />启用增量索引</label>
          </div>
        </fieldset>

        <fieldset class="settings-group">
          <legend>通知与额度</legend>
          <p>选择需要提醒的额度风险；金额填 0 可关闭对应提醒。</p>
          <div class="settings-grid">
            <label>5h 提醒阈值 %<input v-model.number="store.settingsDraft.fiveHourAlertPercent" type="number" min="1" max="99" /></label>
            <label>7d 提醒阈值 %<input v-model.number="store.settingsDraft.sevenDayAlertPercent" type="number" min="1" max="99" /></label>
            <label>本月金额提醒（美元，0 为关闭）<input v-model.number="store.settingsDraft.monthlyAmountAlert" type="number" min="0" max="1000000000" step="1" /></label>
            <label>最低费率覆盖率 %<input v-model.number="store.settingsDraft.minimumRateCoverageAlertPercent" type="number" min="0" max="100" step="1" /></label>
          </div>
          <div class="setting-checks">
            <label><input v-model="store.settingsDraft.notificationsEnabled" type="checkbox" />启用额度通知</label>
            <label><input v-model="store.settingsDraft.quotaForecastAlertsEnabled" type="checkbox" />额度耗尽预警</label>
          </div>
        </fieldset>

        <fieldset class="settings-group">
          <legend>桌面行为</legend>
          <p>控制快捷键、开机启动、主窗口和顶部状态条。</p>
          <div class="settings-grid">
            <label>全局快捷键<select v-model="store.settingsDraft.globalHotKey"><option>Ctrl+U</option><option>Ctrl+Shift+U</option><option>Ctrl+Alt+U</option><option>Ctrl+Shift+C</option><option>Ctrl+Alt+C</option></select></label>
            <label>状态条额度口径<select v-model="store.settingsDraft.statusStripQuotaMode"><option value="remaining">显示剩余</option><option value="used">显示已用</option></select></label>
          </div>
          <div class="setting-checks">
            <label><input v-model="store.settingsDraft.statusStripEnabled" type="checkbox" />启用顶部状态条</label>
            <label><input v-model="store.settingsDraft.statusStripShowTodayTokens" type="checkbox" />状态条显示今日 Token</label>
            <label><input v-model="store.settingsDraft.statusStripPositionLocked" type="checkbox" />锁定状态条位置</label>
            <label><input v-model="store.settingsDraft.startAtLogin" type="checkbox" />开机自动启动</label>
            <label><input v-model="store.settingsDraft.desktopMode" type="checkbox" />启动后置于桌面底层</label>
            <label><input v-model="store.settingsDraft.closeToTray" type="checkbox" />关闭主窗口时隐藏到托盘</label>
          </div>
          <div class="status-strip-control" aria-labelledby="status-strip-control-title">
            <div>
              <strong id="status-strip-control-title">状态条预览与找回</strong>
              <span>
                {{ store.statusStripState?.visible ? '正在显示' : '当前未显示' }}
                · {{ store.statusStripState?.positionMode ?? '状态未知' }}
                · {{ store.statusStripState?.displayName ?? '显示器未知' }}
              </span>
              <small role="status" aria-live="polite" aria-atomic="true">
                {{ store.statusStripState?.message ?? '正在读取状态条状态…' }}
              </small>
            </div>
            <div class="status-strip-actions">
              <button type="button" :disabled="store.isControllingStatusStrip || settingsBusy" @click="store.previewStatusStrip()">
                {{ store.isControllingStatusStrip ? '处理中…' : '立即预览' }}
              </button>
              <button type="button" :disabled="store.isControllingStatusStrip || settingsBusy" @click="store.recoverStatusStrip()">找回状态条</button>
            </div>
          </div>
        </fieldset>

        <fieldset class="settings-group settings-group-pricing">
          <legend>价格与费率</legend>
          <p>设置订阅价格和 API 等价金额；模型级费率在高级设置中按需维护。</p>
          <div class="settings-grid">
            <label>每 1,000 点对应金额（美元）<input v-model.number="store.settingsDraft.amountPerThousandCredits" type="number" min="0.01" max="1000000" step="0.01" /><small class="setting-hint">所有等效金额和订阅收益统一使用 US$</small></label>
            <!-- Two fields, not one: the same plan name prices differently per vendor, and
                 Claude's plan is only auto-priceable when the statusline snapshot exists. -->
            <label>Codex 订阅月费（美元，手动备用）<input v-model.number="store.settingsDraft.codexMonthlySubscriptionAmount" type="number" min="0" max="1000000" step="0.01" @input="store.settingsDraft.codexAutoDetectSubscriptionAmount = false" /><small v-if="activeManualHint" class="setting-hint">{{ snapshot.runtime === 'codex' ? activeManualHint : '当前显示的是 Claude Code，此项影响 Codex 视图' }}</small></label>
            <label>Claude Code 订阅月费（美元，手动备用）<input v-model.number="store.settingsDraft.claudeMonthlySubscriptionAmount" type="number" min="0" max="1000000" step="0.01" @input="store.settingsDraft.claudeAutoDetectSubscriptionAmount = false" /><small v-if="activeManualHint" class="setting-hint">{{ snapshot.runtime === 'claudeCode' ? activeManualHint : '当前显示的是 Codex，此项影响 Claude Code 视图' }}</small></label>
          </div>
          <div class="setting-checks">
            <label><input v-model="store.settingsDraft.codexAutoDetectSubscriptionAmount" type="checkbox" />自动推算 Codex 月费</label>
            <label><input v-model="store.settingsDraft.claudeAutoDetectSubscriptionAmount" type="checkbox" />自动推算 Claude 月费</label>
          </div>

      <details class="rate-editor" :open="rateEditorOpen" @toggle="syncRateEditorState">
        <summary class="rate-editor-summary">
          <span><strong>版本化模型点数费率</strong><small>高级设置 · 默认收起</small></span>
          <em>{{ isPinnedRateCatalog ? `${configuredRateCount} 条锁定快照` : `${customOverrideCount} 条自定义 · ${customRateVersions} 个版本` }}</em>
        </summary>
        <div class="rate-editor-content">
        <div class="rate-catalog-summary">
          <span><small>当前生效</small><strong>{{ isPinnedRateCatalog ? '锁定快照' : '内置 + 覆盖' }}</strong></span>
          <span><small>目录版本</small><strong>{{ isPinnedRateCatalog ? store.settingsDraft.pinnedRateCatalogVersion : (store.rateCatalog?.builtIn.catalogVersion ?? '--') }}</strong></span>
          <span><small>来源</small><strong>{{ isPinnedRateCatalog ? store.settingsDraft.pinnedRateCatalogSource : (store.rateCatalog?.builtIn.source ?? '--') }}</strong></span>
          <span><small>费率总数</small><strong>{{ isPinnedRateCatalog ? configuredRateCount : (store.rateCatalog?.builtIn.rateCount ?? 0) + customOverrideCount }}</strong></span>
          <span v-if="isPinnedRateCatalog"><small>基线版本</small><strong>{{ store.settingsDraft.pinnedRateCatalogBaseVersion ?? '--' }}</strong></span>
        </div>
        <p v-if="isPinnedRateCatalog" class="setting-hint">当前按导入时的完整快照计算；快照未包含的模型不会回落到新版内置费率。为保留版本与来源证明，锁定快照不可直接编辑；恢复内置费率后才可添加自定义覆盖并重新跟随应用更新。</p>
        <div class="rate-actions">
          <button type="button" :disabled="isPinnedRateCatalog || configuredRateCount >= rateEntryLimit || settingsBusy" @click="addCustomRate">添加费率版本</button>
          <button type="button" :disabled="store.settingsDirty || settingsBusy" title="请先保存或放弃当前设置更改" @click="store.runLocalOperation('rates.export')">导出目录</button>
          <button type="button" :disabled="store.settingsDirty || settingsBusy" title="请先保存或放弃当前设置更改" @click="store.runLocalOperation('rates.import')">导入目录</button>
          <button class="warning" type="button" :disabled="store.settingsDirty || settingsBusy" title="请先保存或放弃当前设置更改" @click="store.runLocalOperation('rates.reset')">恢复内置费率</button>
        </div>
        <div class="rate-header"><span>模型名称/别名</span><span>生效日期</span><span>普通输入 / 1M</span><span>缓存输入 / 1M</span><span>输出 / 1M</span><span>版本与来源</span><span /></div>
        <div v-for="(rate, index) in store.settingsDraft.customModelRates" :key="index" class="rate-row">
          <!-- Free text with suggestions, not a select: an override for a model the
               built-in catalog has never heard of is the main reason to add a row
               at all, so the list must not be a whitelist. Choosing a known model
               fills in its current official rate. -->
          <label><span>模型</span><input v-model="rate.model" :disabled="isPinnedRateCatalog" maxlength="100" list="built-in-rate-models" placeholder="例如 claude-sonnet-4" @change="applyBuiltInDefaults(rate)" /></label>
          <label><span>生效日期</span><input v-model="rate.effectiveFrom" :disabled="isPinnedRateCatalog" type="date" /></label>
          <label><span>普通输入 / 1M</span><input v-model.number="rate.inputCreditsPerMillion" :disabled="isPinnedRateCatalog" type="number" min="0" max="1000000" step="0.001" /></label>
          <label><span>缓存输入 / 1M</span><input v-model.number="rate.cachedInputCreditsPerMillion" :disabled="isPinnedRateCatalog" type="number" min="0" max="1000000" step="0.001" /></label>
          <label><span>输出 / 1M</span><input v-model.number="rate.outputCreditsPerMillion" :disabled="isPinnedRateCatalog" type="number" min="0" max="1000000" step="0.001" /></label>
          <div class="rate-metadata">
            <label><span>匹配方式</span><select v-model="rate.matchMode" :disabled="isPinnedRateCatalog"><option value="exact">精确模型</option><option value="prefix">前缀家族</option></select></label>
            <label><span>版本</span><input v-model="rate.catalogVersion" :disabled="isPinnedRateCatalog" maxlength="40" placeholder="custom-2026.07" /></label>
            <label><span>来源</span><input v-model="rate.source" :disabled="isPinnedRateCatalog" maxlength="200" placeholder="用户自定义" /></label>
          </div>
          <button type="button" :disabled="isPinnedRateCatalog" aria-label="删除自定义费率" @click="removeCustomRate(index)">×</button>
        </div>
        <datalist id="built-in-rate-models">
          <option v-for="name in builtInModelNames" :key="name" :value="name" />
        </datalist>
        <p class="setting-hint">新增一行会带出该模型当前生效的官方费率作为起点；改写模型名可重新带出对应默认值。自定义费率会覆盖内置值，留空或填 0 表示该模型不计费。</p>
        <p class="setting-hint">计算会按用量发生日期选择当日已生效的最新版本；自定义费率优先。生效日期留空表示适用于全部历史。“精确模型”最安全；仅当同一费率明确覆盖整个模型家族时才选择“前缀家族”。未知模型仍保留在“未核算 Token”中。</p>
        <p v-if="store.settingsDirty" class="setting-hint">导入、导出或恢复目录前，请先保存或放弃当前页面的更改，避免草稿被覆盖或导出旧数据。</p>
        <p v-if="rateValidationMessage" class="setting-error" role="alert">{{ rateValidationMessage }}</p>
        </div>
      </details>
        </fieldset>
      </div>
      <p class="privacy-note">隐私说明：为生成本机统计，应用会读取必要的会话元数据与 token 字段；不保存、不展示、不上传正文。</p>
      <div class="settings-action-bar">
        <p role="status" aria-live="polite" aria-atomic="true">
          <strong>{{ store.settingsDirty ? '有未保存更改' : '所有更改已保存' }}</strong>
          <span>{{ rateValidationMessage ?? (store.settingsDirty ? '保存后新设置才会完整生效' : '可以安全离开此页面') }}</span>
        </p>
        <div class="settings-actions">
          <button class="discard-settings" :disabled="!store.settingsDirty || settingsBusy" @click="store.resetSettingsDraft()">放弃更改</button>
          <button class="save-settings" :disabled="!store.settingsDirty || settingsBusy || Boolean(rateValidationMessage)" @click="store.saveSettings()">保存设置</button>
        </div>
      </div>
    </article>
  </div>
</template>

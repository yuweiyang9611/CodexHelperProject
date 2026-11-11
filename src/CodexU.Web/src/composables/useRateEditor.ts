import { computed, onScopeDispose, ref, watch } from 'vue'
import { host } from '../host'
import type { useDashboardStore } from '../stores/dashboard'
import type { ModelCreditRate, RateEditorPreview } from '../types'

export function useRateEditor(store: ReturnType<typeof useDashboardStore>) {
  const rateEditorOpen = ref(Boolean(store.settingsDraft?.isRateCatalogPinned))
  const activeRow = ref<ModelCreditRate | null>(null)
  const preview = ref<RateEditorPreview | null>(null)
  const validationMessage = ref<string | null>(null)
  const checking = ref(false)
  const filling = ref(0)
  let disposed = false
  let revision = 0
  let running = false
  const seedRequests = new WeakMap<ModelCreditRate, symbol>()
  const editedRates = computed(() => JSON.stringify(store.settingsDraft?.customModelRates)
    !== JSON.stringify(store.settings?.customModelRates))
  onScopeDispose(() => { disposed = true; revision++ })

  const payload = computed(() => {
    const draft = store.settingsDraft
    if (!draft) return null
    const row = activeRow.value && draft.customModelRates.includes(activeRow.value) ? activeRow.value : null
    return {
      rates: draft.customModelRates.map(rate => ({ ...rate, effectiveFrom: rate.effectiveFrom || null })),
      model: row?.model, effectiveOn: row?.effectiveFrom || null,
    }
  })

  async function validate() {
    if (running) return
    checking.value = true
    validationMessage.value = null
    running = true
    try {
      while (!disposed && payload.value) {
        const target = revision
        try {
          const result = await host.request<RateEditorPreview>('rates.previewDraft', payload.value)
          if (target !== revision) continue
          preview.value = result
          validationMessage.value = result.validationMessage ?? null
        } catch {
          if (target !== revision) continue
          validationMessage.value = '费率预览失败，请重试。'
        }
        break
      }
    } finally {
      checking.value = false
      running = false
    }
  }
  watch([payload, () => store.settingsDraft?.isRateCatalogPinned], () => {
    revision++
    checking.value = true
    preview.value = null
    validationMessage.value = null
    void validate()
  }, { immediate: true })

  function addCustomRate() {
    const seed = preview.value?.nextRate
    if (!seed || !preview.value?.canAdd || checking.value || store.settingsDraft?.isRateCatalogPinned) return
    store.settingsDraft?.customModelRates.push({ ...seed })
  }

  async function applyBuiltInDefaults(rate: ModelCreditRate) {
    const token = Symbol()
    const original = JSON.stringify(rate)
    seedRequests.set(rate, token)
    filling.value++
    try {
      const { builtInRate } = await host.request<RateEditorPreview>('rates.previewDraft', {
        rates: [], model: rate.model, effectiveOn: rate.effectiveFrom || null,
      })
      if (!builtInRate || disposed || !store.settingsDraft?.customModelRates.includes(rate)
        || seedRequests.get(rate) !== token || original !== JSON.stringify(rate)) return
      rate.inputCreditsPerMillion = builtInRate.inputCreditsPerMillion
      rate.cachedInputCreditsPerMillion = builtInRate.cachedInputCreditsPerMillion
      rate.outputCreditsPerMillion = builtInRate.outputCreditsPerMillion
    } catch {
      // The row remains editable; draft validation independently checks its actual prices.
    } finally { filling.value-- }
  }
  return {
    preview, validationMessage, busy: computed(() => checking.value || filling.value > 0),
    blocksSave: computed(() => editedRates.value && (checking.value || filling.value > 0 || !!validationMessage.value)),
    activeRow, rateEditorOpen, applyBuiltInDefaults, addCustomRate, retry: validate,
  }
}

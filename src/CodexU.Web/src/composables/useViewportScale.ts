import { computed } from 'vue'
import { useDashboardStore } from '../stores/dashboard'

/**
 * Applies only the scale explicitly selected by the user. Window-size changes are
 * handled by CSS reflow; silently reducing this value would make text and targets
 * smaller exactly when the window is already constrained.
 */
export function useViewportScale() {
  const store = useDashboardStore()
  const uiScale = computed(() => Math.max(.9, Math.min(1.4,
    (store.settings?.uiScalePercent ?? 110) / 100)))
  const layoutStyle = computed(() => ({
    width: `${100 / uiScale.value}%`,
    minHeight: `${100 / uiScale.value}vh`,
    zoom: uiScale.value,
  }))

  return { uiScale, layoutStyle }
}

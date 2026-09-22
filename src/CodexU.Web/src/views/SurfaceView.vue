<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'

interface Quota { text: string; accessibleText: string; resetText?: string; resetsAt?: string }
interface Presentation {
  runtimeTitle: string; primaryLabel: string; secondaryLabel: string
  primaryQuota: Quota; secondaryQuota: Quota
  statusText: string; statusToolTip: string
}
interface SurfaceData {
  presentation?: Presentation; expanded?: boolean; theme?: string
  statusStripPositionLocked?: boolean
  error?: string; refreshing?: boolean; refreshError?: string
}
declare global {
  interface Window {
    codexUSurface?: { action(name: string): Promise<unknown>; subscribe(fn: (data: SurfaceData) => void): () => void }
  }
}
const data = ref<SurfaceData>({})
const failure = ref('')
const busy = ref(false)
let unsubscribe: (() => void) | undefined
async function action(name: string) {
  try { busy.value = name === 'refresh'; failure.value = ''; await window.codexUSurface?.action(name) }
  catch (error) { failure.value = error instanceof Error ? error.message : '操作失败' }
  finally { busy.value = false }
}
onMounted(() => {
  unsubscribe = window.codexUSurface?.subscribe(value => { data.value = value })
  void action('ready')
})
onUnmounted(() => unsubscribe?.())
</script>

<template>
  <main class="surface" :class="{ light: data.theme === 'light' }" aria-label="额度状态条">
    <header :class="{ locked: data.statusStripPositionLocked }">
      <strong>{{ data.presentation?.runtimeTitle?.replace(' 使用状态', '') || 'codexU' }}</strong>
      <span :title="data.presentation?.primaryQuota.resetText" :aria-label="data.presentation?.primaryQuota.accessibleText">5h 剩余 {{ data.presentation?.primaryQuota.text || '--' }}</span>
      <span :title="data.presentation?.secondaryQuota.resetText" :aria-label="data.presentation?.secondaryQuota.accessibleText">7d 剩余 {{ data.presentation?.secondaryQuota.text || '--' }}</span>
      <button aria-label="展开或折叠状态条" @click="action('expand')">{{ data.expanded ? '收起' : '展开' }}</button>
    </header>
    <section v-if="data.expanded">
      <p><span>5 小时额度</span><strong>{{ data.presentation?.primaryQuota.resetText || '刷新时间未知' }}</strong></p>
      <p><span>7 天额度</span><strong>{{ data.presentation?.secondaryQuota.resetText || '刷新时间未知' }}</strong></p>
      <p :title="data.presentation?.statusToolTip">{{ data.refreshing ? '刷新中…' : (data.refreshError || data.presentation?.statusText || '等待数据') }}</p>
      <nav>
        <button :disabled="busy || data.refreshing" @click="action('refresh')">{{ busy || data.refreshing ? '刷新中' : '刷新' }}</button>
        <button @click="action('open')">打开主界面</button>
        <button @click="action('lock')">{{ data.statusStripPositionLocked ? '解锁位置' : '锁定位置' }}</button>
      </nav>
    </section>
    <p v-if="failure || data.error" role="alert">{{ failure || data.error }}</p>
  </main>
</template>

<style scoped>
.surface { box-sizing: border-box; width: 100vw; min-height: 100vh; padding: 8px 12px; background: #131c2d; color: #edf4ff; font: 12px/1.45 system-ui; border: 1px solid #425774; border-radius: 10px; }
.surface.light { background: #f5f8ff; color: #172238; }
header { display: flex; align-items: center; justify-content: space-between; gap: 8px; min-height: 27px; -webkit-app-region: drag; }
header.locked { -webkit-app-region: no-drag; }
button { -webkit-app-region: no-drag; cursor: pointer; padding: 4px 8px; border: 1px solid #647a99; border-radius: 5px; color: inherit; background: transparent; font: inherit; }
button:focus-visible { outline: 2px solid #59c7ff; outline-offset: 2px; }
section p { display: flex; justify-content: space-between; margin: 10px 0; }
nav { display: flex; flex-wrap: wrap; gap: 6px; }
</style>

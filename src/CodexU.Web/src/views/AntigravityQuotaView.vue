<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import UiIcon from '../components/UiIcon.vue'
import { useQuotaClock } from '../composables/useQuotaClock'
import { resetTime } from '../format'
import { host } from '../host'
import { quotaSampleTime } from '../quotaPresentation'
import type { AntigravityQuotaBucket, AntigravityQuotaSnapshot } from '../types'

const quota = ref<AntigravityQuotaSnapshot | null>(null)
const loading = ref(false)
let disposed = false
const groups = computed(() => quota.value?.status === 'ready' ? quota.value.groups : [])
const now = useQuotaClock(() => groups.value.flatMap(group => group.buckets.map(bucket => bucket.resetsAt)))
const amountFormat = new Intl.NumberFormat('zh-CN', { maximumFractionDigits: 2 })

const notice = computed(() => {
  if (loading.value || !quota.value) return '正在读取 Antigravity 额度…'
  switch (quota.value?.status) {
    case 'notInstalled': return '未找到 Antigravity CLI。安装 agy 后，用订阅账号登录，再点击刷新。'
    case 'unsupportedVersion': return '当前 Antigravity CLI 版本不支持后台读取额度。请升级到 1.1.11 或更新版本。'
    case 'noQuota': return 'Antigravity CLI 暂未返回订阅额度。请在 agy 中确认登录账号和 /usage 结果。'
    case 'ready': return groups.value.length ? '' : 'Antigravity CLI 暂未返回额度分组。'
    default: return '暂时无法读取 Antigravity 额度。请确认 agy 已登录订阅账号，再点击刷新。'
  }
})

function remainingLabel(bucket: AntigravityQuotaBucket) {
  if (bucket.disabled) return '未启用'
  if (bucket.remainingPercent != null) {
    if (bucket.remainingPercent > 0 && bucket.remainingPercent < 0.01) return '剩余 < 0.01%'
    return `剩余 ${amountFormat.format(bucket.remainingPercent)}%`
  }
  if (bucket.remainingAmount != null) return `剩余 ${amountFormat.format(bucket.remainingAmount)}`
  return '剩余额度未知'
}

async function refresh() {
  if (loading.value) return
  loading.value = true
  try {
    const result = await host.request<AntigravityQuotaSnapshot>('antigravity.getQuota')
    if (!disposed) quota.value = result
  } catch {
    if (!disposed) quota.value = { status: 'unavailable', groups: [] }
  } finally {
    if (!disposed) loading.value = false
  }
}

onMounted(() => void refresh())
onUnmounted(() => { disposed = true })
</script>

<template>
  <section class="antigravity-view">
    <header class="quota-heading">
      <div>
        <h2>Antigravity 额度</h2>
        <p>
          来源 Antigravity CLI
          <template v-if="quota?.status === 'ready' && quota.observedAt">
            · 读取于 <time :datetime="quota.observedAt">{{ quotaSampleTime(quota.observedAt) }}</time>
          </template>
        </p>
      </div>
      <button type="button" :aria-disabled="loading" @click="refresh">
        <UiIcon name="refresh" :size="15" />
        刷新额度
      </button>
    </header>

    <p class="quota-notice" role="status" :class="{ 'sr-only': !notice }">{{ notice || 'Antigravity 额度已更新' }}</p>

    <div v-if="groups.length" class="quota-groups" :aria-busy="loading">
      <article v-for="(group, groupIndex) in groups" :key="`${groupIndex}:${group.name}`" class="quota-group">
        <h3>{{ group.name }}</h3>
        <ul>
          <li v-for="(bucket, bucketIndex) in group.buckets" :key="`${bucketIndex}:${bucket.label}`" class="quota-bucket">
            <div class="bucket-row">
              <span>{{ bucket.label }}</span>
              <strong>{{ remainingLabel(bucket) }}</strong>
            </div>
            <div v-if="!bucket.disabled && bucket.remainingPercent != null" class="bucket-meter" aria-hidden="true">
              <span :style="{ width: `${bucket.remainingPercent}%` }" />
            </div>
            <p v-if="!bucket.disabled" class="bucket-reset">
              <template v-if="bucket.resetsAt">
                <time :datetime="bucket.resetsAt">{{ quotaSampleTime(bucket.resetsAt) }}</time>
                · {{ resetTime(bucket.resetsAt, now) }}
              </template>
              <template v-else>重置时间不可用</template>
            </p>
          </li>
        </ul>
      </article>
    </div>
  </section>
</template>

<style scoped>
.antigravity-view { display: grid; gap: 14px; padding: 16px; }
.quota-heading { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 10px 16px; }
.quota-heading h2 { margin: 0; font-size: 18px; }
.quota-heading p { margin: 5px 0 0; color: var(--text-secondary); font-size: 12px; }
.quota-heading button { display: inline-flex; align-items: center; gap: 7px; min-height: 36px; padding: 6px 12px; border: 1px solid var(--stroke); border-radius: 8px; color: var(--text-primary); background: var(--surface-subtle); cursor: pointer; font-size: 12px; }
.quota-heading button[aria-disabled="true"] { cursor: wait; }
.quota-notice { margin: 0; padding: 12px; border: 1px solid var(--stroke); border-radius: 10px; color: var(--text-secondary); background: var(--surface-subtle); font-size: 13px; }
.quota-groups { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 290px), 1fr)); gap: 12px; }
.quota-group { min-width: 0; padding: 14px; border: 1px solid var(--stroke); border-radius: 12px; background: var(--surface-subtle); }
.quota-group h3 { margin: 0; color: var(--text-primary); font-size: 14px; overflow-wrap: anywhere; }
.quota-group ul { display: grid; gap: 12px; margin: 12px 0 0; padding: 0; list-style: none; }
.quota-bucket + .quota-bucket { padding-top: 12px; border-top: 1px solid var(--stroke-subtle); }
.bucket-row { display: flex; flex-wrap: wrap; align-items: baseline; justify-content: space-between; gap: 4px 8px; color: var(--text-secondary); font-size: 13px; overflow-wrap: anywhere; }
.bucket-row strong { color: var(--text-primary); font-variant-numeric: tabular-nums; }
.bucket-meter { height: 5px; margin-top: 7px; overflow: hidden; border-radius: 999px; background: var(--stroke); }
.bucket-meter span { display: block; height: 100%; border-radius: inherit; background: var(--blue); }
.bucket-reset { margin: 6px 0 0; color: var(--text-secondary); font-size: 12px; overflow-wrap: anywhere; }
</style>

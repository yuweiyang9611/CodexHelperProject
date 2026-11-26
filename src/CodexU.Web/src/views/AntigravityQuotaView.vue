<script setup lang="ts">
import { computed } from 'vue'
import QuotaRing from '../components/QuotaRing.vue'
import { useQuotaClock } from '../composables/useQuotaClock'
import { quotaSampleTime } from '../quotaPresentation'
import type { AntigravityQuotaSnapshot } from '../types'

const props = defineProps<{ quota: AntigravityQuotaSnapshot | null; loading: boolean; embedded?: boolean }>()
const groups = computed(() => props.quota?.status === 'ready' ? props.quota.groups : [])
const now = useQuotaClock(() => groups.value.flatMap(group => group.buckets.map(bucket => bucket.resetsAt)))

const notice = computed(() => {
  if (props.loading || !props.quota) return '正在读取 Antigravity 额度…'
  switch (props.quota.status) {
    case 'notInstalled': return '未找到 Antigravity CLI。安装 agy 后，用订阅账号登录，再点击刷新。'
    case 'unsupportedVersion': return '当前 Antigravity CLI 版本不支持后台读取额度。请升级到 1.1.11 或更新版本。'
    case 'noQuota': return 'Antigravity CLI 暂未返回订阅额度。请在 agy 中确认登录账号和 /usage 结果。'
    case 'modelInvoked': return 'Antigravity CLI 把额度查询当作普通提问执行，调用了模型并消耗了额度。为避免重复消耗，已停止查询；升级 agy 后会自动重试。'
    case 'ready': return groups.value.length ? '' : 'Antigravity CLI 暂未返回额度分组。'
    default: return '暂时无法读取 Antigravity 额度。请确认 agy 已登录订阅账号，再点击刷新。'
  }
})
</script>

<template>
  <section class="antigravity-view" :class="{ 'glass-card': !embedded, 'embedded-quota': embedded }">
    <header class="quota-heading">
      <h2 v-if="!embedded">Antigravity 额度</h2>
      <p>
        来源 Antigravity CLI
        <template v-if="quota?.status === 'ready' && quota.observedAt">
          · 读取于 <time :datetime="quota.observedAt">{{ quotaSampleTime(quota.observedAt) }}</time>
        </template>
      </p>
    </header>

    <p class="quota-notice" role="status" :class="{ 'sr-only': !notice }">{{ notice || 'Antigravity 额度已更新' }}</p>

    <div v-if="groups.length" class="quota-groups" :aria-busy="loading">
      <article v-for="(group, groupIndex) in groups" :key="`${groupIndex}:${group.name}`" class="quota-group">
        <h3>{{ group.name }}</h3>
        <ul class="quota-buckets" role="list">
          <li v-for="(bucket, bucketIndex) in group.buckets" :key="`${bucketIndex}:${bucket.label}`" class="quota-bucket">
            <QuotaRing :label="bucket.label" :bucket="bucket" :now="now" :color="bucketIndex % 2 === 0 ? 'blue' : 'violet'" />
          </li>
        </ul>
      </article>
    </div>
  </section>
</template>

<style scoped>
.antigravity-view { display: grid; gap: 14px; padding: 16px; }
.quota-heading h2 { margin: 0; font-size: 18px; }
.quota-heading p { margin: 5px 0 0; color: var(--text-secondary); font-size: 12px; line-height: 1.5; }
.quota-notice { margin: 0; padding: 12px; border: 1px solid var(--stroke); border-radius: 10px; color: var(--text-secondary); background: var(--surface-subtle); font-size: 13px; }
.quota-groups { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 320px), 1fr)); gap: 16px; }
.quota-group { min-width: 0; border-top: 1px solid var(--stroke); padding-top: 12px; }
.quota-group h3 { margin: 0 0 8px; color: var(--text-primary); font-size: 14px; overflow-wrap: anywhere; }
.quota-buckets { display: flex; flex-wrap: wrap; justify-content: center; gap: 8px 4px; margin: 0; padding: 0; list-style: none; }
.quota-bucket { max-width: 100%; }
.embedded-quota { padding: 0; gap: 12px; }
.embedded-quota .quota-groups { grid-template-columns: minmax(0, 1fr); gap: 12px; }
.embedded-quota .quota-buckets { --bucket-ring-long-value-size: 20px; }
</style>
